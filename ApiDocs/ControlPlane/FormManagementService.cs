using System.Text;
using System.Text.Json;
using Business;
using Models;

namespace ApiDocs.ControlPlane
{
    public sealed record FormListView(IReadOnlyList<FormSummary> Forms, bool CanEdit, bool CanPublish);

    public sealed record FormEditView(string Name, int? LatestRevision, RevisionContent? Current, IReadOnlyList<RevisionInfo> History, bool CanEdit, bool CanPublish, FormValidation? Validation);

    public sealed record SaveResult(int Revision, string Status, FormValidation Validation);

    public sealed record DiffLine(char Kind, int? OldLine, int? NewLine, string Text);

    public sealed record ApplyReport(IReadOnlyList<PackageInfo> Packages);

    public sealed record PackageFile(string FileName, byte[] Bytes);

    /// <summary>
    /// Soạn thảo form (revision bất biến, khóa lạc quan, diff, publish) và gói DEV→PROD kèm nhật ký đồng bộ.
    /// Chỉ NHÂN VIÊN (staff) mới thấy nội dung form (có SQL): khách hàng không bao giờ, dù vai trò partner có forms.read.
    /// Quyền theo dự án: forms.read xem, forms.edit lưu nháp/kiểm, forms.publish publish + tạo gói + ghi nhận kết quả áp.
    /// Không sửa tại chỗ: mỗi lần lưu là một revision mới; quay lại bản cũ = lưu bản cũ thành revision mới
    /// </summary>
    public sealed class FormManagementService(IControlPlaneStore store, ProjectAccessService access, TimeProvider time)
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

        // ---------- Quyền ----------
        private async Task<(AccountAccess? Actor, string? Project)> RequireAsync(int actorId, int projectId, string permission, CancellationToken cancellationToken)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            return actor is { Account.Kind: AccountKind.Staff } && actor.Can(projectId, permission) ? (actor, actor.For(projectId)!.Project.Code) : (null, null);
        }

        private Task Audit(int actorId, int projectId, string action, string target, string outcome, string? detail, CancellationToken cancellationToken) =>
            store.WriteAuditAsync(new AuditRecord(actorId, projectId, action, target, outcome, detail), cancellationToken);

        // ---------- Danh sách / chi tiết ----------
        public async Task<OpResult> ListFormsAsync(int actorId, int projectId, CancellationToken cancellationToken = default)
        {
            var (actor, _) = await RequireAsync(actorId, projectId, PortalPermissions.FormsRead, cancellationToken);
            if (actor == null)
            {
                return new OpResult("FORBIDDEN");
            }
            return new OpResult("SUCCESS", new FormListView(await store.ListFormsAsync(projectId, cancellationToken), actor.Can(projectId, PortalPermissions.FormsEdit), actor.Can(projectId, PortalPermissions.FormsPublish)));
        }

        /// <param name="revision">Null = bản mới nhất. Chọn bản cũ để soạn lại: lưu sẽ tạo revision mới dựa trên bản mới nhất</param>
        public async Task<OpResult> GetFormAsync(int actorId, int projectId, string form, int? revision, CancellationToken cancellationToken = default)
        {
            var (actor, _) = await RequireAsync(actorId, projectId, PortalPermissions.FormsRead, cancellationToken);
            if (actor == null)
            {
                return new OpResult("FORBIDDEN");
            }
            if (!FormDefinitionValidator.IsValidName(form))
            {
                return new OpResult("INVALID_INPUT");
            }

            IReadOnlyList<RevisionInfo> history = await store.GetRevisionsAsync(projectId, form, cancellationToken);
            int? latest = history.Count == 0 ? null : history[0].Revision;
            RevisionContent? current = latest == null ? null : await store.GetRevisionAsync(projectId, form, revision ?? latest.Value, cancellationToken);
            if (latest != null && current == null)
            {
                return new OpResult("NOT_FOUND");
            }
            FormValidation? validation = current == null ? null : FormDefinitionValidator.Validate(form, current.Content);
            return new OpResult("SUCCESS", new FormEditView(form, latest, current, history, actor.Can(projectId, PortalPermissions.FormsEdit), actor.Can(projectId, PortalPermissions.FormsPublish), validation));
        }

        // ---------- Kiểm / lưu ----------
        public async Task<OpResult> ValidateAsync(int actorId, int projectId, string form, string? content, CancellationToken cancellationToken = default)
        {
            var (actor, _) = await RequireAsync(actorId, projectId, PortalPermissions.FormsEdit, cancellationToken);
            return actor == null ? new OpResult("FORBIDDEN") : new OpResult("SUCCESS", FormDefinitionValidator.Validate(form, FormDefinitionValidator.Normalize(content ?? "")));
        }

        public async Task<OpResult> SaveAsync(int actorId, int projectId, string form, int? baseRevision, string? content, CancellationToken cancellationToken = default)
        {
            var (actor, project) = await RequireAsync(actorId, projectId, PortalPermissions.FormsEdit, cancellationToken);
            if (actor == null)
            {
                await Audit(actorId, projectId, "form.save", form, "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }

            string normalized = FormDefinitionValidator.Normalize(content ?? "");
            if (!FormDefinitionValidator.IsValidName(form) || Encoding.UTF8.GetByteCount(normalized) > FormDefinitionValidator.MaxBytes)
            {
                return new OpResult("INVALID_INPUT");
            }

            // Tên khác nhau chỉ ở chữ hoa/thường là cùng một file trên Windows và cùng một route: không cho tạo thành hai form
            if ((await store.ListFormsAsync(projectId, cancellationToken)).Any(f => string.Equals(f.Name, form, StringComparison.OrdinalIgnoreCase) && f.Name != form))
            {
                await Audit(actorId, projectId, "form.save", $"{project}/{form}", "REJECTED", "name differs only by case", cancellationToken);
                return new OpResult("NAME_CASE_CONFLICT");
            }

            FormValidation validation = FormDefinitionValidator.Validate(form, normalized);
            // Bản hợp lệ là "validated" (đủ điều kiện publish); bản còn lỗi vẫn lưu được ở dạng "draft" để soạn tiếp
            string status = validation.IsValid ? "validated" : "draft";
            string sha = FormDefinitionValidator.Sha256(normalized);

            int? revision = await store.AddRevisionAsync(projectId, form, baseRevision, normalized, sha, status, actorId, cancellationToken);
            if (revision == null)
            {
                await Audit(actorId, projectId, "form.save", $"{project}/{form}", "REJECTED", "conflict", cancellationToken);
                int? latest = (await store.GetRevisionsAsync(projectId, form, cancellationToken)).FirstOrDefault()?.Revision;
                return new OpResult("CONFLICT", latest);
            }

            await Audit(actorId, projectId, "form.save", $"{project}/{form}", "SUCCESS", $"revision {revision}, {status}, sha {sha[..12]}", cancellationToken);
            return new OpResult("SUCCESS", new SaveResult(revision.Value, status, validation));
        }

        // ---------- Publish ----------
        public async Task<OpResult> PublishAsync(int actorId, int projectId, string form, int revision, CancellationToken cancellationToken = default)
        {
            var (actor, project) = await RequireAsync(actorId, projectId, PortalPermissions.FormsPublish, cancellationToken);
            if (actor == null)
            {
                await Audit(actorId, projectId, "form.publish", form, "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }
            if (!FormDefinitionValidator.IsValidName(form))
            {
                return new OpResult("INVALID_INPUT");
            }

            RevisionContent? target = await store.GetRevisionAsync(projectId, form, revision, cancellationToken);
            if (target == null)
            {
                return new OpResult("NOT_FOUND");
            }
            if (target.Info.Status != "validated")
            {
                await Audit(actorId, projectId, "form.publish", $"{project}/{form}", "REJECTED", $"revision {revision} is {target.Info.Status}", cancellationToken);
                return new OpResult("INVALID_STATE");
            }

            // Kiểm lại ngay lúc publish (luật có thể đã đổi từ lúc lưu) và bắt trùng route với các form đang hiệu lực khác
            FormValidation validation = FormDefinitionValidator.Validate(form, target.Content);
            if (!validation.IsValid)
            {
                await Audit(actorId, projectId, "form.publish", $"{project}/{form}", "REJECTED", "validation failed", cancellationToken);
                return new OpResult("INVALID_FORM", validation);
            }
            IReadOnlyList<ActiveForm> active = await store.GetActiveFormsAsync(projectId, cancellationToken);
            string? conflict = RouteConflict(validation.Form!, active.Where(a => !string.Equals(a.Name, form, StringComparison.Ordinal)));
            if (conflict != null)
            {
                await Audit(actorId, projectId, "form.publish", $"{project}/{form}", "REJECTED", conflict, cancellationToken);
                return new OpResult("ROUTE_CONFLICT", conflict);
            }

            if (!await store.PublishRevisionAsync(projectId, form, revision, actorId, cancellationToken))
            {
                return new OpResult("INVALID_STATE");
            }
            await Audit(actorId, projectId, "form.publish", $"{project}/{form}", "SUCCESS", $"revision {revision}, route {validation.Form!.Route}", cancellationToken);
            return new OpResult("SUCCESS");
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

        // ---------- Diff ----------
        public async Task<OpResult> DiffAsync(int actorId, int projectId, string form, int fromRevision, int toRevision, CancellationToken cancellationToken = default)
        {
            var (actor, _) = await RequireAsync(actorId, projectId, PortalPermissions.FormsRead, cancellationToken);
            if (actor == null)
            {
                return new OpResult("FORBIDDEN");
            }
            if (!FormDefinitionValidator.IsValidName(form))
            {
                return new OpResult("INVALID_INPUT");
            }
            RevisionContent? from = await store.GetRevisionAsync(projectId, form, fromRevision, cancellationToken);
            RevisionContent? to = await store.GetRevisionAsync(projectId, form, toRevision, cancellationToken);
            if (from == null || to == null)
            {
                return new OpResult("NOT_FOUND");
            }

            string[] a = from.Content.Split('\n'), b = to.Content.Split('\n');
            if (a.Length > MaxDiffLines || b.Length > MaxDiffLines)
            {
                return new OpResult("INVALID_INPUT");
            }
            return new OpResult("SUCCESS", Diff(a, b));
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

        // ---------- Package ----------
        public async Task<OpResult> ListPackagesAsync(int actorId, int projectId, CancellationToken cancellationToken = default)
        {
            var (actor, _) = await RequireAsync(actorId, projectId, PortalPermissions.FormsRead, cancellationToken);
            return actor == null ? new OpResult("FORBIDDEN") : new OpResult("SUCCESS", await store.ListPackagesAsync(projectId, cancellationToken));
        }

        /// <summary>Gói = mọi form đang hiệu lực (revision publish mới nhất) của dự án; tất cả phải còn hợp lệ và không trùng route</summary>
        public async Task<OpResult> CreatePackageAsync(int actorId, int projectId, CancellationToken cancellationToken = default)
        {
            var (actor, project) = await RequireAsync(actorId, projectId, PortalPermissions.FormsPublish, cancellationToken);
            if (actor == null)
            {
                await Audit(actorId, projectId, "package.create", "*", "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }

            IReadOnlyList<ActiveForm> active = await store.GetActiveFormsAsync(projectId, cancellationToken);
            if (active.Count == 0)
            {
                return new OpResult("NO_FORMS");
            }

            List<ImportForm> parsed = [];
            foreach (ActiveForm form in active)
            {
                FormValidation check = FormDefinitionValidator.Validate(form.Name, form.Content);
                if (!check.IsValid)
                {
                    await Audit(actorId, projectId, "package.create", project!, "REJECTED", $"{form.Name} invalid", cancellationToken);
                    return new OpResult("INVALID_FORM", form.Name);
                }
                parsed.Add(check.Form!);
            }
            ImportFormCatalog catalog = ImportFormCatalog.Build(parsed);
            if (catalog.Errors.Count > 0)
            {
                await Audit(actorId, projectId, "package.create", project!, "REJECTED", "route conflict", cancellationToken);
                return new OpResult("ROUTE_CONFLICT", string.Join("; ", catalog.Errors.Values));
            }

            DateTime now = time.GetUtcNow().UtcDateTime;
            int id = await store.CreatePackageAsync(projectId, actorId, packageId =>
            {
                var (json, sha) = FormPackage.CreateManifest(project!, packageId, now, active.Select(a => (a.Name, a.Revision, a.Content)));
                return new PackageDraft(json, sha, active.Select(a => a.RevisionId).ToList());
            }, cancellationToken);

            await Audit(actorId, projectId, "package.create", $"{project}#{id}", "SUCCESS", $"{active.Count} form", cancellationToken);
            await store.AddSyncLogAsync(new SyncLogRecord(projectId, "DEV", "EXPORT", id, "portal", actor.Account.Login, "OK", $"{active.Count} form"), cancellationToken);
            return new OpResult("SUCCESS", id);
        }

        /// <summary>Dựng lại zip từ manifest đã lưu nguyên văn và kiểm toàn vẹn (hash từng form, hash manifest) trước khi giao</summary>
        public async Task<OpResult> DownloadPackageAsync(int actorId, int projectId, int packageId, CancellationToken cancellationToken = default)
        {
            var (actor, project) = await RequireAsync(actorId, projectId, PortalPermissions.FormsPublish, cancellationToken);
            if (actor == null)
            {
                await Audit(actorId, projectId, "package.download", packageId.ToString(), "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }
            PackageRecord? record = await store.GetPackageAsync(projectId, packageId, cancellationToken);
            if (record == null)
            {
                return new OpResult("NOT_FOUND");
            }

            byte[] zip = FormPackage.Assemble(record.ManifestJson, record.Items.Select(i => (i.Form, i.Content)));
            try
            {
                PackageContents check = FormPackage.Read(zip);
                if (!string.Equals(check.ManifestSha256, record.Info.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormPackageException("HASH_MISMATCH", "manifest khác bản đã lưu");
                }
            }
            catch (FormPackageException ex)
            {
                await Audit(actorId, projectId, "package.download", $"{project}#{packageId}", "REJECTED", ex.Code, cancellationToken);
                return new OpResult("INTEGRITY");
            }

            await Audit(actorId, projectId, "package.download", $"{project}#{packageId}", "SUCCESS", null, cancellationToken);
            return new OpResult("SUCCESS", new PackageFile($"{project}-package-{packageId}.zip", zip));
        }

        // ---------- Nhật ký đồng bộ ----------
        /// <summary>
        /// Nhập file kết quả do CLI apply-package xuất. Chỉ nhận khi gói thuộc dự án và SHA-256 manifest trong kết quả khớp bản đã lưu
        /// (không dựng được kết quả cho gói khác); tên người thao tác trong file do CLI khai nên chỉ ghi nhận, người nhập mới chịu trách nhiệm trong audit
        /// </summary>
        public async Task<OpResult> RecordApplyResultAsync(int actorId, int projectId, string? json, CancellationToken cancellationToken = default)
        {
            var (actor, project) = await RequireAsync(actorId, projectId, PortalPermissions.FormsPublish, cancellationToken);
            if (actor == null)
            {
                await Audit(actorId, projectId, "sync.import", "*", "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxResultBytes)
            {
                return new OpResult("INVALID_INPUT");
            }

            ApplyResult? result;
            try
            {
                result = JsonSerializer.Deserialize<ApplyResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return new OpResult("INVALID_INPUT");
            }
            if (result?.Files == null || result.Files.Count is 0 or > FormPackage.MaxEntries || !Environments.Contains(result.Environment) || result.Files.Any(f => !FileOutcomes.Contains(f.Outcome)))
            {
                return new OpResult("INVALID_INPUT");
            }

            PackageRecord? package = await store.GetPackageAsync(projectId, result.PackageId, cancellationToken);
            if (package == null || !string.Equals(package.Info.ManifestSha256, result.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.Project, project, StringComparison.OrdinalIgnoreCase)
                || result.Files.Any(f => !package.Items.Any(i => i.Form == f.Name)))
            {
                await Audit(actorId, projectId, "sync.import", $"{project}#{result.PackageId}", "REJECTED", "package mismatch", cancellationToken);
                return new OpResult("PACKAGE_MISMATCH");
            }

            bool ok = result.Success && result.Files.All(f => f.Outcome != "FAILED");
            string counts = string.Join(", ", result.Files.GroupBy(f => f.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            string problems = string.Join("; ", result.Files.Where(f => f.Outcome is "FAILED" or "NOT_APPLIED").Take(3).Select(f => $"{f.Name}: {AccountLoginService.NoControl(f.Message ?? f.Outcome)}"));
            string detail = AccountLoginService.NoControl($"{counts}; nhập bởi {actor.Account.Login}" + (problems.Length > 0 ? $"; {problems}" : ""));

            // Kết quả chạy thử (chưa ghi file) ghi là VERIFY, kết quả đã ghi file ghi là APPLY
            string eventName = result.Applied ? "APPLY" : "VERIFY";
            await store.AddSyncLogAsync(new SyncLogRecord(projectId, result.Environment, eventName, result.PackageId, "cli", Clean(result.Operator), ok ? "OK" : "FAILED", detail), cancellationToken);
            await Audit(actorId, projectId, "sync.import", $"{project}#{result.PackageId}", "SUCCESS", $"{eventName} {result.Environment} {(ok ? "OK" : "FAILED")}", cancellationToken);
            return new OpResult("SUCCESS", eventName);
        }

        public async Task<OpResult> ListSyncLogAsync(int actorId, int? projectId, string? env, string? outcome, int page, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor is not { Account.Kind: AccountKind.Staff })
            {
                return new OpResult("FORBIDDEN");
            }
            List<int> scope = actor.Projects.Where(p => p.Permissions.Contains(PortalPermissions.FormsRead)).Select(p => p.Project.Id).ToList();
            if (scope.Count == 0 || projectId is int chosen && !scope.Contains(chosen))
            {
                return new OpResult("FORBIDDEN");
            }
            string e = (env ?? "").Trim().ToUpperInvariant(), o = (outcome ?? "").Trim().ToUpperInvariant();
            if (e.Length > 0 && !Environments.Contains(e) || o.Length > 0 && o is not ("OK" or "FAILED"))
            {
                return new OpResult("INVALID_INPUT");
            }
            return new OpResult("SUCCESS", await store.ListSyncLogAsync(scope, projectId, e, o, Math.Max(page, 1), SyncPageSize, cancellationToken));
        }

        /// <summary>Tên người thao tác do CLI khai: bỏ ký tự điều khiển và cắt ngắn</summary>
        private static string Clean(string? value) => AccountLoginService.NoControl(value ?? "")[..Math.Min(AccountLoginService.NoControl(value ?? "").Length, 64)];
    }
}
