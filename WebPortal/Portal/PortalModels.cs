namespace WebPortal.Portal
{
    /// <summary>Ba loại tài khoản cố định (ADR-0001). Quyền suy từ loại, không có bảng vai trò</summary>
    public static class AccountKind
    {
        /// <summary>Quản trị portal, duy nhất, đăng nhập /cpanel</summary>
        public const string SuperAdmin = "SA";
        /// <summary>Quản trị dự án, ngoài mapping FBO, không có quyền api; đăng nhập /{ma_da}</summary>
        public const string Admin = "ADMIN";
        /// <summary>User Api của dự án (portalApiUser), đăng nhập /{ma_da}</summary>
        public const string Api = "API";
    }

    public sealed record PortalAccount(int Id, string Login, string DisplayName, string Kind, bool Active, string PasswordHash, bool MustChangePassword, DateTime? LastLoginAt);

    public sealed record PortalProject(int Id, string MaDa, string ApiFolder, string ApiBaseUrl, bool Active, DateTime? LastCheckAt, string? LastCheckResult);

    public sealed record ApiUserIp(string Ip, string? Description);

    /// <summary>User Api trong bảng ánh xạ của portal (nguồn sự thật). Forms rỗng = mọi form, Ips rỗng = mọi IP</summary>
    public sealed record PortalApiUser(int Id, int ProjectId, string UserName, string NameErp, string? DisplayName, string PasswordHash, bool Active,
        int SyncVersion, int SyncedVersion, DateTime? SyncedAt, string? SyncError, IReadOnlyList<string> Forms, IReadOnlyList<ApiUserIp> Ips)
    {
        public bool InSync => SyncedVersion >= SyncVersion;
    }

    public sealed record NewApiUser(string UserName, string NameErp, string? DisplayName, string PasswordHash, IReadOnlyList<string> Forms, IReadOnlyList<ApiUserIp> Ips);

    public sealed record SessionRecord(Guid Id, string AccountType, int AccountId, int? ProjectId, DateTime CreatedAt, DateTime LastSeenAt,
        string? IpAddress, string? UserAgent, DateTime? RevokedAt, string? RevokedReason);

    public sealed record ChallengeRecord(Guid Id, string AccountType, int AccountId, int? ProjectId, Guid OldSessionId, string? IpAddress, string? UserAgent,
        DateTime CreatedAt, DateTime ExpiresAt, DateTime? ClosedAt, string? Outcome);

    public sealed record LicenseRecord(Guid Id, int ProjectId, string Kid, string MacList, string? MachineName, DateTime IssuedAt, string? IssuedBy,
        DateTime? ExpiresAt, DateTime? RevokedAt, string? RevokedReason);

    public sealed record AuditRecord(string? ActorType, int? ActorId, string? ActorLogin, int? ProjectId, string Action, string? Target, string Outcome,
        string? Detail = null, string? IpAddress = null, string? ActingAs = null, Guid? SessionId = null);

    public sealed record AuditEntryView(long Id, DateTime At, string? ActorType, string? ActorLogin, int? ProjectId, string? ProjectCode, string Action,
        string? Target, string Outcome, string? Detail, string? IpAddress, string? ActingAs);

    public sealed record AuditQuery(DateTime From, DateTime To, int? ProjectId, string? Actor, string? ActionPrefix, string? Outcome);

    public sealed record AuditPage(int Total, IReadOnlyList<AuditEntryView> Items);

    /// <summary>Danh tính đang đăng nhập, dựng lại từ cookie + phiên ở mỗi request</summary>
    public sealed record PortalIdentity(string Kind, int AccountId, string Login, string DisplayName, int? ProjectId, string? MaDa, Guid SessionId)
    {
        public bool IsSuperAdmin => Kind == AccountKind.SuperAdmin;
        public bool IsAdmin => Kind == AccountKind.Admin;
        public bool IsApi => Kind == AccountKind.Api;
    }

    /// <summary>Kết quả nghiệp vụ: Code = SUCCESS hoặc mã lỗi; Data tùy thao tác</summary>
    public sealed record OpResult(string Code, object? Data = null)
    {
        public bool Success => Code == "SUCCESS";

        public static readonly OpResult Ok = new("SUCCESS");

        public static OpResult Fail(string code) => new(code);
    }
}
