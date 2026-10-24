using System.Text;
using System.Text.Json;
using Business;
using Models;

namespace WebPortal.Portal
{
    public sealed record FormListView(IReadOnlyList<FormSummary> Forms);

    public sealed record FormEditView(string Name, int? LatestRevision, RevisionContent? Current, IReadOnlyList<RevisionInfo> History, FormValidation? Validation);

    public sealed record SaveResult(int Revision, string Status, FormValidation Validation);

    public sealed record DiffLine(char Kind, int? OldLine, int? NewLine, string Text);

    public sealed record PackageFile(string FileName, byte[] Bytes);

    /// <summary>
    /// Soạn khai báo form XML theo từng dự án (nội bộ DEV/UAT): revision bất biến, khóa lạc quan, diff, publish, gói DEV→PROD.
    /// Chỉ SA và Admin của dự án (user Api không bao giờ thấy nội dung form có SQL).
    /// Áp lên DEV: portal ghi thẳng App_Data\Form của folder dự án. Áp lên PROD: PortalApp tại máy khách áp gói (không qua portal)
    /// </summary>
    public sealed class FormManagementService(IPortalStore store, IProjectConnectionResolver resolver, TimeProvider time)
    {
        public const int MaxDiffLines = 2000;
        public const int MaxResultBytes = 64 * 1024;
        public const int SyncPageSize = 50;
        private static readonly string[] Environments = ["DEV", "PROD"];
        private static readonly string[] FileOutcomes = ["NEW", "CHANGED", "SAME", "FAILED", "NOT_APPLIED"];

        /// <summary>Khung form hợp lệ tối thiểu cho form mới (tên form quyết định route; sửa lại field và SQL cho đúng nghiệp vụ)</summary>
        public const string NewFormTemplate = """
            <?xml version="1.0" encoding="utf-8"?>
            <import xmlns="urn:schemas-fast-com:fast-api" type="List" key="ma_kh" jsonNaming="camelCase" columnNaming="snake_case" description="Mô tả form">
              <fields name="filter">
                <field name="customerId" fastName="ma_kh" maxLength="16" upperCase="true" description="Mã khách" example="KH001"/>
              </fields>
              <query action="processing">
                <text>
                  <![CDATA[
            SELECT 'SUCCESS' AS code, '' AS message, 'customers' AS tables
            SELECT RTRIM(a.ma_kh) AS customerId, a.ten_kh AS customerName FROM dmkh a WHERE a.status = '1'
                  ]]>
                </text>
              </query>
            </import>
            """;

        private Task Audit(PortalIdentity actor, PortalProject project, string action, string target, string outcome, string? detail, CancellationToken cancellationToken) =>
            store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, action, target, outcome, detail), cancellationToken);

        public async Task<OpResult> ListFormsAsync(PortalIdentity actor, PortalProject project, CancellationToken cancellationToken = default) =>
            PortalGuard.CanAdmin(actor, project.Id) ? new OpResult("SUCCESS", new FormListView(await store.ListFormsAsync(project.Id, cancellationToken))) : OpResult.Fail("FORBIDDEN");

        /// <param name="revision">Null = bản mới nhất. Chọn bản cũ để soạn lại: lưu sẽ tạo revision mới dựa trên bản mới nhất</param>
        public async Task<OpResult> GetFormAsync(PortalIdentity actor, PortalProject project, string form, int? revision, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            if (!FormDefinitionValidator.IsValidName(form))
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            IReadOnlyList<RevisionInfo> history = await store.GetRevisionsAsync(project.Id, form, cancellationToken);
            int? latest = history.Count == 0 ? null : history[0].Revision;
            RevisionContent? current = latest == null ? null : await store.GetRevisionAsync(project.Id, form, revision ?? latest.Value, cancellationToken);
            if (latest != null && current == null)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            // Form chưa có revision nào trên portal mà đã có file trong folder dự án: mở nội dung file để soạn tiếp
            if (current == null)
            {
                string? fromFolder = ReadFromFolder(project, form);
                if (fromFolder != null)
                {
                    return new OpResult("SUCCESS", new FormEditView(form, null, new RevisionContent(new RevisionInfo(0, 0, null, "folder", "", DateTime.MinValue, null, null, null), fromFolder),
                        history, FormDefinitionValidator.Validate(form, fromFolder)));
                }
            }
            FormValidation? validation = current == null ? null : FormDefinitionValidator.Validate(form, current.Content);
            return new OpResult("SUCCESS", new FormEditView(form, latest, current, history, validation));
        }

        public async Task<OpResult> ValidateAsync(PortalIdentity actor, PortalProject project, string form, string? content, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            return PortalGuard.CanAdmin(actor, project.Id)
                ? new OpResult("SUCCESS", FormDefinitionValidator.Validate(form, FormDefinitionValidator.Normalize(content ?? "")))
                : OpResult.Fail("FORBIDDEN");
        }

        public async Task<OpResult> SaveAsync(PortalIdentity actor, PortalProject project, string form, int? baseRevision, string? content, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                await Audit(actor, project, "form.save", form, "DENIED", null, cancellationToken);
                return OpResult.Fail("FORBIDDEN");
            }
            string normalized = FormDefinitionValidator.Normalize(content ?? "");
            if (!FormDefinitionValidator.IsValidName(form) || Encoding.UTF8.GetByteCount(normalized) > FormDefinitionValidator.MaxBytes)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            // Tên khác nhau chỉ ở chữ hoa/thường là cùng một file trên Windows và cùng một route
            if ((await store.ListFormsAsync(project.Id, cancellationToken)).Any(f => string.Equals(f.Name, form, StringComparison.OrdinalIgnoreCase) && f.Name != form))
            {
                await Audit(actor, project, "form.save", $"{project.MaDa}/{form}", "REJECTED", "name differs only by case", cancellationToken);
                return OpResult.Fail("NAME_CASE_CONFLICT");
            }

            FormValidation validation = FormDefinitionValidator.Validate(form, normalized);
            string status = validation.IsValid ? "validated" : "draft";
            string sha = FormDefinitionValidator.Sha256(normalized);
            int? revision = await store.AddRevisionAsync(project.Id, form, baseRevision, normalized, sha, status, actor.AccountId, cancellationToken);
            if (revision == null)
            {
                await Audit(actor, project, "form.save", $"{project.MaDa}/{form}", "REJECTED", "conflict", cancellationToken);
                return new OpResult("CONFLICT", (await store.GetRevisionsAsync(project.Id, form, cancellationToken)).FirstOrDefault()?.Revision);
            }
            await Audit(actor, project, "form.save", $"{project.MaDa}/{form}", "SUCCESS", $"revision {revision}, {status}, sha {sha[..12]}", cancellationToken);
            return new OpResult("SUCCESS", new SaveResult(revision.Value, status, validation));
        }

        public async Task<OpResult> PublishAsync(PortalIdentity actor, PortalProject project, string form, int revision, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                await Audit(actor, project, "form.publish", form, "DENIED", null, cancellationToken);
                return OpResult.Fail("FORBIDDEN");
            }
            if (!FormDefinitionValidator.IsValidName(form))
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            RevisionContent? target = await store.GetRevisionAsync(project.Id, form, revision, cancellationToken);
            if (target == null)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            if (target.Info.Status != "validated")
            {
                await Audit(actor, project, "form.publish", $"{project.MaDa}/{form}", "REJECTED", $"revision {revision} is {target.Info.Status}", cancellationToken);
                return OpResult.Fail("INVALID_STATE");
            }
            FormValidation validation = FormDefinitionValidator.Validate(form, target.Content);
            if (!validation.IsValid)
            {
                await Audit(actor, project, "form.publish", $"{project.MaDa}/{form}", "REJECTED", "validation failed", cancellationToken);
                return new OpResult("INVALID_FORM", validation);
            }
            IReadOnlyList<ActiveForm> active = await store.GetActiveFormsAsync(project.Id, cancellationToken);
            string? conflict = RouteConflict(validation.Form!, active.Where(a => !string.Equals(a.Name, form, StringComparison.Ordinal)));
            if (conflict != null)
            {
                await Audit(actor, project, "form.publish", $"{project.MaDa}/{form}", "REJECTED", conflict, cancellationToken);
                return new OpResult("ROUTE_CONFLICT", conflict);
            }
            if (!await store.PublishRevisionAsync(project.Id, form, revision, actor.AccountId, cancellationToken))
            {
                return OpResult.Fail("INVALID_STATE");
            }
            await Audit(actor, project, "form.publish", $"{project.MaDa}/{form}", "SUCCESS", $"revision {revision}, route {validation.Form!.Route}", cancellationToken);
            return OpResult.Ok;
        }

        private static string? RouteConflict(ImportForm candidate, IEnumerable<ActiveForm> others)
        {
            List<ImportForm> forms = [candidate];
            foreach (ActiveForm other in others)
            {
                if (FormDefinitionValidator.Validate(other.Name, other.Content).Form is { } parsed)
                {
                    forms.Add(parsed);
                }
            }
            ImportFormCatalog catalog = ImportFormCatalog.Build(forms);
            return catalog.Errors.TryGetValue(candidate.Name, out string? error) ? error : null;
        }

        public async Task<OpResult> DiffAsync(PortalIdentity actor, PortalProject project, string form, int fromRevision, int toRevision, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            if (!FormDefinitionValidator.IsValidName(form))
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            RevisionContent? from = await store.GetRevisionAsync(project.Id, form, fromRevision, cancellationToken);
            RevisionContent? to = await store.GetRevisionAsync(project.Id, form, toRevision, cancellationToken);
            if (from == null || to == null)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            string[] a = from.Content.Split('\n'), b = to.Content.Split('\n');
            return a.Length > MaxDiffLines || b.Length > MaxDiffLines ? OpResult.Fail("INVALID_INPUT") : new OpResult("SUCCESS", Diff(a, b));
        }

        /// <summary>So sánh theo dòng (LCS), bỏ phần đầu/cuối giống nhau trước để bảng quy hoạch động nhỏ</summary>
        public static IReadOnlyList<DiffLine> Diff(string[] a, string[] b)
        {
            int start = 0;
            while (start < a.Length && start < b.Length && a[start] == b[start])
            {
                start++;
            }
            int endA = a.Length, endB = b.Length;
            while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
            {
                endA--;
                endB--;
            }
            int n = endA - start, m = endB - start;
            int[,] lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }
            List<DiffLine> result = [];
            for (int i = 0; i < start; i++)
            {
                result.Add(new DiffLine(' ', i + 1, i + 1, a[i]));
            }
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y])
                {
                    result.Add(new DiffLine(' ', start + x + 1, start + y + 1, a[start + x]));
                    x++;
                    y++;
                }
                else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
                {
                    result.Add(new DiffLine('+', null, start + y + 1, b[start + y]));
                    y++;
                }
                else
                {
                    result.Add(new DiffLine('-', start + x + 1, null, a[start + x]));
                    x++;
                }
            }
            for (int i = endA; i < a.Length; i++)
            {
                result.Add(new DiffLine(' ', i + 1, i - endA + endB + 1, a[i]));
            }
            return result;
        }

        // ---------- Gói ----------
        public async Task<OpResult> ListPackagesAsync(PortalIdentity actor, PortalProject project, CancellationToken cancellationToken = default) =>
            PortalGuard.CanAdmin(actor, project.Id) ? new OpResult("SUCCESS", await store.ListPackagesAsync(project.Id, cancellationToken)) : OpResult.Fail("FORBIDDEN");

        /// <summary>Gói = mọi form đang hiệu lực (revision publish mới nhất) của dự án; tất cả phải còn hợp lệ và không trùng route</summary>
        public async Task<OpResult> CreatePackageAsync(PortalIdentity actor, PortalProject project, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                await Audit(actor, project, "package.create", "*", "DENIED", null, cancellationToken);
                return OpResult.Fail("FORBIDDEN");
            }
            IReadOnlyList<ActiveForm> active = await store.GetActiveFormsAsync(project.Id, cancellationToken);
            if (active.Count == 0)
            {
                return OpResult.Fail("NO_FORMS");
            }
            List<ImportForm> parsed = [];
            foreach (ActiveForm form in active)
            {
                FormValidation check = FormDefinitionValidator.Validate(form.Name, form.Content);
                if (!check.IsValid)
                {
                    await Audit(actor, project, "package.create", project.MaDa, "REJECTED", $"{form.Name} invalid", cancellationToken);
                    return new OpResult("INVALID_FORM", form.Name);
                }
                parsed.Add(check.Form!);
            }
            ImportFormCatalog catalog = ImportFormCatalog.Build(parsed);
            if (catalog.Errors.Count > 0)
            {
                await Audit(actor, project, "package.create", project.MaDa, "REJECTED", "route conflict", cancellationToken);
                return new OpResult("ROUTE_CONFLICT", string.Join("; ", catalog.Errors.Values));
            }

            DateTime now = time.GetUtcNow().UtcDateTime;
            int id = await store.CreatePackageAsync(project.Id, actor.AccountId, packageId =>
            {
                var (json, sha) = FormPackage.CreateManifest(project.MaDa, packageId, now, active.Select(a => (a.Name, a.Revision, a.Content)));
                return new PackageDraft(json, sha, active.Select(a => a.RevisionId).ToList());
            }, cancellationToken);
            await Audit(actor, project, "package.create", $"{project.MaDa}#{id}", "SUCCESS", $"{active.Count} form", cancellationToken);
            await store.AddSyncLogAsync(new SyncLogRecord(project.Id, "DEV", "EXPORT", id, "portal", actor.Login, "OK", $"{active.Count} form"), cancellationToken);
            return new OpResult("SUCCESS", id);
        }

        /// <summary>Dựng lại zip từ manifest đã lưu nguyên văn và kiểm toàn vẹn trước khi giao</summary>
        public async Task<OpResult> DownloadPackageAsync(PortalIdentity actor, PortalProject project, int packageId, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            byte[]? zip = await BuildZipAsync(project, packageId, cancellationToken);
            if (zip == null)
            {
                return OpResult.Fail("INTEGRITY");
            }
            await Audit(actor, project, "package.download", $"{project.MaDa}#{packageId}", "SUCCESS", null, cancellationToken);
            return new OpResult("SUCCESS", new PackageFile($"{project.MaDa}-package-{packageId}.zip", zip));
        }

        /// <summary>
        /// Áp gói lên folder DEV/UAT của dự án ngay từ portal (thay cho CLI): ghi App_Data\Form qua file tạm, sao lưu bản cũ, WebApi tự nạp lại.
        /// Không dùng cho PROD (PROD áp bằng PortalApp tại máy khách)
        /// </summary>
        public async Task<OpResult> ApplyToFolderAsync(PortalIdentity actor, PortalProject project, int packageId, bool apply, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            PackageRecord? record = await store.GetPackageAsync(project.Id, packageId, cancellationToken);
            byte[]? zip = record == null ? null : await BuildZipAsync(project, packageId, cancellationToken);
            if (record == null || zip == null)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            ProjectConnection connection;
            try
            {
                connection = resolver.Resolve(project);
            }
            catch (ProjectFolderException ex)
            {
                return new OpResult("PROJECT_UNREACHABLE", ex.Message);
            }
            if (string.Equals(connection.Environment, "PROD", StringComparison.OrdinalIgnoreCase))
            {
                return OpResult.Fail("PROD_FOLDER");
            }
            ApplyResult result;
            try
            {
                result = FormPackageApplier.Run(zip, record.Info.ManifestSha256, connection.FormDirectory, apply, "DEV", actor.Login, time);
            }
            catch (FormPackageException ex)
            {
                return new OpResult("PACKAGE_REJECTED", ex.Message);
            }
            await RecordAsync(actor, project, result, "portal", cancellationToken);
            return new OpResult("SUCCESS", result);
        }

        /// <summary>
        /// Nhập file kết quả PortalApp xuất sau khi áp gói ở PROD. Chỉ nhận khi gói thuộc dự án và SHA-256 manifest khớp bản đã lưu;
        /// tên người thao tác trong file do PortalApp ghi nên chỉ để ghi nhận, người nhập mới chịu trách nhiệm trong audit
        /// </summary>
        public async Task<OpResult> RecordApplyResultAsync(PortalIdentity actor, PortalProject project, string? json, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                await Audit(actor, project, "sync.import", "*", "DENIED", null, cancellationToken);
                return OpResult.Fail("FORBIDDEN");
            }
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxResultBytes)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            ApplyResult? result;
            try
            {
                result = JsonSerializer.Deserialize<ApplyResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            if (result?.Files == null || result.Files.Count is 0 or > FormPackage.MaxEntries || !Environments.Contains(result.Environment) || result.Files.Any(f => !FileOutcomes.Contains(f.Outcome)))
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            PackageRecord? package = await store.GetPackageAsync(project.Id, result.PackageId, cancellationToken);
            if (package == null || !string.Equals(package.Info.ManifestSha256, result.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.Project, project.MaDa, StringComparison.OrdinalIgnoreCase)
                || result.Files.Any(f => !package.Items.Any(i => i.Form == f.Name)))
            {
                await Audit(actor, project, "sync.import", $"{project.MaDa}#{result.PackageId}", "REJECTED", "package mismatch", cancellationToken);
                return OpResult.Fail("PACKAGE_MISMATCH");
            }
            string eventName = await RecordAsync(actor, project, result, "app", cancellationToken);
            return new OpResult("SUCCESS", eventName);
        }

        public async Task<OpResult> ListSyncLogAsync(PortalIdentity actor, PortalProject project, int page, CancellationToken cancellationToken = default) =>
            PortalGuard.CanAdmin(actor, project.Id)
                ? new OpResult("SUCCESS", await store.ListSyncLogAsync(project.Id, Math.Max(page, 1), SyncPageSize, cancellationToken))
                : OpResult.Fail("FORBIDDEN");

        private async Task<string> RecordAsync(PortalIdentity actor, PortalProject project, ApplyResult result, string source, CancellationToken cancellationToken)
        {
            bool ok = result.Success && result.Files.All(f => f.Outcome != "FAILED");
            string counts = string.Join(", ", result.Files.GroupBy(f => f.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            string problems = string.Join("; ", result.Files.Where(f => f.Outcome is "FAILED" or "NOT_APPLIED").Take(3)
                .Select(f => $"{f.Name}: {PortalLoginService.NoControl(f.Message ?? f.Outcome)}"));
            string detail = PortalLoginService.NoControl($"{counts}; ghi nhận bởi {actor.Login}" + (problems.Length > 0 ? $"; {problems}" : ""));
            string eventName = result.Applied ? "APPLY" : "VERIFY";
            string operatorName = PortalLoginService.NoControl(result.Operator ?? "");
            await store.AddSyncLogAsync(new SyncLogRecord(project.Id, result.Environment, eventName, result.PackageId, source,
                operatorName.Length > 64 ? operatorName[..64] : operatorName, ok ? "OK" : "FAILED", detail), cancellationToken);
            await Audit(actor, project, "sync.record", $"{project.MaDa}#{result.PackageId}", "SUCCESS", $"{eventName} {result.Environment} {(ok ? "OK" : "FAILED")}", cancellationToken);
            return eventName;
        }

        private async Task<byte[]?> BuildZipAsync(PortalProject project, int packageId, CancellationToken cancellationToken)
        {
            PackageRecord? record = await store.GetPackageAsync(project.Id, packageId, cancellationToken);
            if (record == null)
            {
                return null;
            }
            byte[] zip = FormPackage.Assemble(record.ManifestJson, record.Items.Select(i => (i.Form, i.Content)));
            try
            {
                PackageContents check = FormPackage.Read(zip);
                return string.Equals(check.ManifestSha256, record.Info.ManifestSha256, StringComparison.OrdinalIgnoreCase) ? zip : null;
            }
            catch (FormPackageException)
            {
                return null;
            }
        }

        private string? ReadFromFolder(PortalProject project, string form)
        {
            try
            {
                string path = Path.Combine(resolver.Resolve(project).FormDirectory, form + ".xml");
                return File.Exists(path) && new FileInfo(path).Length <= FormDefinitionValidator.MaxBytes ? FormDefinitionValidator.Normalize(File.ReadAllText(path)) : null;
            }
            catch (Exception ex) when (ex is ProjectFolderException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
