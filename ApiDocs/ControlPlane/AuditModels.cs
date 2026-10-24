namespace ApiDocs.ControlPlane
{
    /// <param name="Person">Khớp tên đăng nhập người thao tác HOẶC đối tượng (để thấy cả lần đăng nhập sai với tên không tồn tại)</param>
    /// <param name="ActionPrefix">Tiền tố hành động, vd "account." hoặc "login"</param>
    public sealed record AuditQuery(DateTime From, DateTime To, int? ProjectId, string Person, string ActionPrefix, string Outcome);

    public sealed record AuditEntry(long Id, DateTime At, int? ActorAccountId, string? ActorLogin, int? ProjectId, string? ProjectCode, string Action, string? Target, string Outcome, string? Detail);

    public sealed record AuditPage(int Total, IReadOnlyList<AuditEntry> Items);

    public sealed record AccountCounts(int StaffActive, int StaffLocked, int CustomerActive, int CustomerLocked);

    public sealed record ProjectStat(int ProjectId, string Code, string Name, bool Active, int ActiveCustomers, int ActiveStaff, int Denied7Days);

    /// <summary>Số liệu tổng quan lấy trực tiếp từ bảng portal; mục nào người xem không có quyền thì null</summary>
    public sealed record Dashboard(AccountCounts? Accounts, IReadOnlyList<ProjectStat> Projects, int? Denied7Days, int? FailedLogins24Hours, IReadOnlyList<AuditEntry>? Recent, bool Central);
}
