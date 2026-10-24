namespace WebPortal.Portal
{
    /// <summary>
    /// Ma trận quyền cố định (ADR-0001). Cookie đã được kiểm (phiên, tài khoản, Admin còn gán dự án) trước khi tới đây
    /// </summary>
    public static class PortalGuard
    {
        /// <summary>Quản trị một dự án: SA, hoặc Admin của đúng dự án đó. Admin không có quyền gọi api</summary>
        public static bool CanAdmin(PortalIdentity? actor, int projectId) =>
            actor != null && (actor.IsSuperAdmin || actor.IsAdmin && actor.ProjectId == projectId);

        /// <summary>User Api của đúng dự án</summary>
        public static bool IsApiOf(PortalIdentity? actor, int projectId) => actor is { IsApi: true } && actor.ProjectId == projectId;

        public static AuditRecord Audit(PortalIdentity? actor, int? projectId, string action, string? target, string outcome, string? detail = null, string? ip = null) =>
            new(actor?.Kind, actor?.AccountId, actor?.Login, projectId, action, target, outcome, detail, ip,
                actor is { IsSuperAdmin: true } && projectId != null ? AccountKind.SuperAdmin : null, actor?.SessionId);
    }
}
