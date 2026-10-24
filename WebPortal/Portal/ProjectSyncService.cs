using DataAccess;

namespace WebPortal.Portal
{
    public sealed record DriftItem(string UserName, string Problem);

    /// <summary>
    /// Đồng bộ MỘT CHIỀU portal => dự án. Portal là nguồn sự thật của user Api và quyền form/IP; ai sửa thẳng ở DB dự án sẽ bị ghi đè
    /// ở lần đẩy kế tiếp. Không bao giờ đọc từ dự án để ghi ngược về portal (báo lệch chỉ đọc)
    /// </summary>
    public sealed class ProjectSyncService(IPortalStore store, IProjectConnectionResolver resolver, ILogger<ProjectSyncService> logger)
    {
        /// <summary>Đẩy trạng thái hiện tại của một user. Lỗi thì giữ thay đổi ở portal, ghi sync_error, trả SYNC_FAILED</summary>
        public async Task<OpResult> PushAsync(PortalProject project, int apiUserId, CancellationToken cancellationToken = default)
        {
            PortalApiUser? user = await store.FindApiUserAsync(apiUserId, cancellationToken);
            if (user == null || user.ProjectId != project.Id)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            try
            {
                ProjectConnection connection = resolver.Resolve(project);
                await connection.Admin.UpsertUserAsync(new ApiUserSpec(user.UserName, user.NameErp, user.Active, user.Forms,
                    user.Ips.Select(i => (i.Ip, i.Description)).ToList()), cancellationToken);
                await store.MarkApiUserSyncAsync(user.Id, user.SyncVersion, null, cancellationToken);
                return OpResult.Ok;
            }
            catch (Exception ex) when (ex is ProjectFolderException or Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Đồng bộ {User} xuống dự án {Project} lỗi: {Error}", user.UserName, project.MaDa, ex.Message);
                string message = ex is ProjectFolderException ? ex.Message : "Không ghi được DB dự án (kết nối hoặc quyền SQL).";
                await store.MarkApiUserSyncAsync(user.Id, user.SyncVersion, message, cancellationToken);
                return new OpResult("SYNC_FAILED", message);
            }
        }

        /// <summary>Đẩy lại mọi user của dự án (nút "Đồng bộ lại tất cả"). Trả số user lỗi</summary>
        public async Task<int> PushAllAsync(PortalProject project, CancellationToken cancellationToken = default)
        {
            int failed = 0;
            foreach (PortalApiUser user in await store.ListApiUsersAsync(project.Id, cancellationToken))
            {
                if (!(await PushAsync(project, user.Id, cancellationToken)).Success)
                {
                    failed++;
                }
            }
            return failed;
        }

        /// <summary>So portal với DB dự án (chỉ đọc). Cách sửa duy nhất là đẩy lại từ portal</summary>
        public async Task<IReadOnlyList<DriftItem>> DriftAsync(PortalProject project, CancellationToken cancellationToken = default)
        {
            ProjectConnection connection = resolver.Resolve(project);
            IReadOnlyList<ProjectApiUser> remote = await connection.Admin.ListUsersAsync(cancellationToken);
            Dictionary<string, ProjectApiUser> byName = remote.ToDictionary(u => u.Name, StringComparer.OrdinalIgnoreCase);
            List<DriftItem> drift = [];
            foreach (PortalApiUser user in await store.ListApiUsersAsync(project.Id, cancellationToken))
            {
                if (!byName.Remove(user.UserName, out ProjectApiUser? actual))
                {
                    drift.Add(new DriftItem(user.UserName, "chưa có trên DB dự án"));
                    continue;
                }
                if (!string.Equals(actual.NameErp, user.NameErp, StringComparison.OrdinalIgnoreCase))
                {
                    drift.Add(new DriftItem(user.UserName, $"name_erp trên dự án là '{actual.NameErp}', portal là '{user.NameErp}'"));
                }
                if (actual.Active != user.Active)
                {
                    drift.Add(new DriftItem(user.UserName, $"trạng thái trên dự án là {(actual.Active ? "hoạt động" : "khóa")}"));
                }
                if (!SameSet(actual.Forms, user.Forms))
                {
                    drift.Add(new DriftItem(user.UserName, $"quyền form trên dự án: {Describe(actual.Forms)}; portal: {Describe(user.Forms)}"));
                }
                if (!SameSet(actual.Ips, user.Ips.Select(i => i.Ip).ToList()))
                {
                    drift.Add(new DriftItem(user.UserName, $"IP trên dự án: {Describe(actual.Ips)}; portal: {Describe(user.Ips.Select(i => i.Ip).ToList())}"));
                }
            }
            foreach (ProjectApiUser orphan in byName.Values)
            {
                drift.Add(new DriftItem(orphan.Name, "chỉ có trên DB dự án, không có trên portal (tạo ngoài portal)"));
            }
            return drift;
        }

        private static bool SameSet(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b) =>
            new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);

        private static string Describe(IReadOnlyCollection<string> items) => items.Count == 0 ? "tất cả" : string.Join(", ", items);
    }
}
