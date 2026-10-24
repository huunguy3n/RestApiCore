namespace WebPortal.Portal
{
    public sealed record ProjectView(PortalProject Project, string Name, bool QldaActive, IReadOnlyList<PortalAccount> Admins);

    /// <summary>
    /// Dự án trên portal: mã lấy từ nbdmda (status = 1), folder Api do SA/Admin khai (gợi ý từ dir_src_web), địa chỉ WebApi để "Gọi thử".
    /// Tạo dự án chỉ SA; sửa folder/địa chỉ: SA hoặc Admin của dự án; ngừng/mở dự án chỉ SA
    /// </summary>
    public sealed class ProjectService(IPortalStore store, IQldaDirectory qlda, IProjectConnectionResolver resolver)
    {
        public async Task<IReadOnlyList<ProjectView>> ListAsync(PortalIdentity actor, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PortalProject> projects = await store.ListProjectsAsync(cancellationToken);
            if (!actor.IsSuperAdmin)
            {
                projects = projects.Where(p => PortalGuard.CanAdmin(actor, p.Id)).ToList();
            }
            List<ProjectView> views = [];
            foreach (PortalProject project in projects)
            {
                QldaProject? registered = await SafeFindAsync(project.MaDa, cancellationToken);
                views.Add(new ProjectView(project, registered?.Name ?? project.MaDa, registered?.Active == true, await store.GetProjectAdminsAsync(project.Id, cancellationToken)));
            }
            return views;
        }

        /// <summary>Dự án QLDA đang hoạt động chưa có trên portal (để SA chọn)</summary>
        public async Task<IReadOnlyList<QldaProject>> CandidatesAsync(CancellationToken cancellationToken = default)
        {
            HashSet<string> existing = new((await store.ListProjectsAsync(cancellationToken)).Select(p => p.MaDa), StringComparer.OrdinalIgnoreCase);
            return (await qlda.ListActiveAsync(cancellationToken)).Where(p => !existing.Contains(p.MaDa) && ProjectCode.IsValid(p.MaDa)).ToList();
        }

        public async Task<string?> NameOfAsync(string maDa, CancellationToken cancellationToken = default) => (await SafeFindAsync(maDa, cancellationToken))?.Name;

        /// <summary>
        /// Gợi ý folder Api từ nbdmda.dir_src_web: thư mục anh em "restApi" của thư mục program
        /// (\\server\CustomerPro\FBO\AQTC\program\ → \\server\CustomerPro\FBO\AQTC\restApi). Người dùng vẫn phải xác nhận
        /// </summary>
        public static string SuggestApiFolder(string? dirSrcWeb)
        {
            string trimmed = (dirSrcWeb ?? "").Trim().TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return "";
            }
            int slash = trimmed.LastIndexOfAny(['\\', '/']);
            return slash > 0 ? trimmed[..(slash + 1)] + "restApi" : trimmed + "\\restApi";
        }

        public async Task<OpResult> CreateAsync(PortalIdentity actor, string? maDa, string? apiFolder, string? apiBaseUrl, string? ip, CancellationToken cancellationToken = default)
        {
            if (!actor.IsSuperAdmin)
            {
                return OpResult.Fail("FORBIDDEN");
            }
            maDa = (maDa ?? "").Trim();
            if (!ProjectCode.IsValid(maDa))
            {
                return OpResult.Fail("INVALID_PROJECT_CODE");
            }
            QldaProject? registered = await qlda.FindAsync(maDa, cancellationToken);
            if (registered is not { Active: true })
            {
                return OpResult.Fail("QLDA_INACTIVE");
            }
            OpResult checkedInput = await CheckInputAsync(maDa, apiFolder, apiBaseUrl, cancellationToken);
            if (!checkedInput.Success)
            {
                return checkedInput;
            }
            int? id = await store.CreateProjectAsync(registered.MaDa, apiFolder!.Trim(), Url(apiBaseUrl), actor.AccountId, cancellationToken);
            if (id == null)
            {
                return OpResult.Fail("DUPLICATE");
            }
            await store.SetProjectCheckAsync(id.Value, (string)checkedInput.Data!, cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, id, "project.create", registered.MaDa, "SUCCESS", null, ip), cancellationToken);
            return new OpResult("SUCCESS", id.Value);
        }

        public async Task<OpResult> UpdateAsync(PortalIdentity actor, int projectId, string? apiFolder, string? apiBaseUrl, bool? active, string? ip, CancellationToken cancellationToken = default)
        {
            PortalProject? project = await store.FindProjectAsync(projectId, cancellationToken);
            if (project == null || !PortalGuard.CanAdmin(actor, projectId) || active != null && !actor.IsSuperAdmin)
            {
                return OpResult.Fail(project == null ? "NOT_FOUND" : "FORBIDDEN");
            }
            OpResult checkedInput = await CheckInputAsync(project.MaDa, apiFolder, apiBaseUrl, cancellationToken);
            if (!checkedInput.Success)
            {
                return checkedInput;
            }
            await store.UpdateProjectAsync(projectId, apiFolder!.Trim(), Url(apiBaseUrl), active ?? project.Active, cancellationToken);
            await store.SetProjectCheckAsync(projectId, (string)checkedInput.Data!, cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, projectId, "project.update", project.MaDa, "SUCCESS", active == null ? null : active.Value ? "mở" : "ngừng", ip), cancellationToken);
            return OpResult.Ok;
        }

        /// <summary>Kiểm lại kết nối folder + DB của dự án, lưu kết quả</summary>
        public async Task<FolderCheck> CheckAsync(PortalProject project, CancellationToken cancellationToken = default)
        {
            FolderCheck check = await resolver.CheckAsync(project.ApiFolder, project.MaDa, cancellationToken);
            await store.SetProjectCheckAsync(project.Id, (check.Ok ? "OK: " : "LỖI: ") + check.Message, cancellationToken);
            return check;
        }

        private async Task<OpResult> CheckInputAsync(string maDa, string? apiFolder, string? apiBaseUrl, CancellationToken cancellationToken)
        {
            if (!IsValidBaseUrl(apiBaseUrl))
            {
                return OpResult.Fail("INVALID_URL");
            }
            FolderCheck check = await resolver.CheckAsync((apiFolder ?? "").Trim(), maDa, cancellationToken);
            return check.Ok ? new OpResult("SUCCESS", "OK: " + check.Message) : new OpResult("INVALID_FOLDER", check.Message);
        }

        /// <summary>Origin WebApi: https (http chỉ cho localhost / mạng nội bộ có tên máy), không user:pass, không query/fragment</summary>
        public static bool IsValidBaseUrl(string? url) =>
            Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "https" or "http"
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

        private static string Url(string? url) => (url ?? "").Trim().TrimEnd('/');

        private async Task<QldaProject?> SafeFindAsync(string maDa, CancellationToken cancellationToken)
        {
            try
            {
                return await qlda.FindAsync(maDa, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException)
            {
                return null;
            }
        }
    }
}
