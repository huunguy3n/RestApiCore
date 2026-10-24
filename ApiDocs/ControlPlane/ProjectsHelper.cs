using Models;

namespace ApiDocs.ControlPlane
{
    public static class ProjectsHelper
    {
        /// <summary>Dự án mà tài khoản là NHÂN VIÊN và có forms.read (khách hàng luôn rỗng): dùng cho bộ chọn dự án ở các trang form / gói / đồng bộ</summary>
        public static async Task<IReadOnlyList<ProjectInfo>> StaffProjectsAsync(ProjectAccessService access, int accountId, CancellationToken cancellationToken)
        {
            AccountAccess? actor = await access.GetAccessAsync(accountId, cancellationToken);
            return actor is { Account.Kind: AccountKind.Staff }
                ? actor.Projects.Where(p => p.Permissions.Contains(PortalPermissions.FormsRead)).Select(p => p.Project).ToList()
                : [];
        }
    }
}
