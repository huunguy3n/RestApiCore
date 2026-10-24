namespace WebPortal.Portal
{
    /// <summary>
    /// Cổng lưu trữ PortalDb (sql/portal/001-002). Không bao giờ chứa connection string hay khóa của dự án.
    /// Tên đăng nhập là duy nhất toàn portal (SA, Admin, user Api mọi dự án) — kiểm ở LoginNameTakenAsync trước khi tạo
    /// </summary>
    public interface IPortalStore
    {
        /// <summary>
        /// Null nếu PortalDb đã có bảng tối thiểu của sql/portal/001 (portalAccount, portalApiUser, portalSession, …).
        /// Chuỗi mô tả lỗi nếu chưa chạy script hoặc còn schema cũ
        /// </summary>
        Task<string?> CheckSchemaAsync(CancellationToken cancellationToken = default);

        // ----- Tài khoản SA / ADMIN -----
        Task<PortalAccount?> FindAccountByLoginAsync(string login, CancellationToken cancellationToken = default);
        Task<PortalAccount?> FindAccountAsync(int id, CancellationToken cancellationToken = default);
        Task<bool> SuperAdminExistsAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<PortalAccount>> ListAdminsAsync(CancellationToken cancellationToken = default);

        /// <summary>Null nếu tên đã dùng ở bất kỳ đâu (kể cả user Api), hoặc tạo SA thứ hai</summary>
        Task<int?> CreateAccountAsync(string login, string displayName, string kind, string passwordHash, int? createdBy, CancellationToken cancellationToken = default);
        Task SetAccountActiveAsync(int id, bool active, CancellationToken cancellationToken = default);
        Task SetAccountPasswordAsync(int id, string passwordHash, bool mustChange, CancellationToken cancellationToken = default);
        Task TouchAccountLoginAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>Tên đăng nhập đã có ở portalAccount hoặc portalApiUser (mọi dự án), không phân biệt hoa thường</summary>
        Task<bool> LoginNameTakenAsync(string login, CancellationToken cancellationToken = default);

        // ----- Dự án -----
        Task<PortalProject?> FindProjectAsync(int id, CancellationToken cancellationToken = default);
        Task<PortalProject?> FindProjectByCodeAsync(string maDa, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<PortalProject>> ListProjectsAsync(CancellationToken cancellationToken = default);

        /// <summary>Null nếu trùng mã dự án</summary>
        Task<int?> CreateProjectAsync(string maDa, string apiFolder, string apiBaseUrl, int createdBy, CancellationToken cancellationToken = default);
        Task UpdateProjectAsync(int id, string apiFolder, string apiBaseUrl, bool active, CancellationToken cancellationToken = default);
        Task SetProjectCheckAsync(int id, string result, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<int>> GetAdminProjectIdsAsync(int accountId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<PortalAccount>> GetProjectAdminsAsync(int projectId, CancellationToken cancellationToken = default);
        Task<bool> IsProjectAdminAsync(int accountId, int projectId, CancellationToken cancellationToken = default);
        Task AddProjectAdminAsync(int projectId, int accountId, int grantedBy, CancellationToken cancellationToken = default);
        Task RemoveProjectAdminAsync(int projectId, int accountId, CancellationToken cancellationToken = default);

        // ----- User Api (bảng ánh xạ) -----
        Task<IReadOnlyList<PortalApiUser>> ListApiUsersAsync(int projectId, CancellationToken cancellationToken = default);
        Task<PortalApiUser?> FindApiUserAsync(int id, CancellationToken cancellationToken = default);
        Task<PortalApiUser?> FindApiUserByNameAsync(int projectId, string userName, CancellationToken cancellationToken = default);

        /// <summary>Null nếu tên đã dùng ở bất kỳ đâu</summary>
        Task<int?> CreateApiUserAsync(int projectId, NewApiUser user, int createdBy, CancellationToken cancellationToken = default);

        /// <summary>Đổi name_erp / trạng thái / danh sách form / IP trong một transaction, tăng sync_version</summary>
        Task UpdateApiUserAsync(int id, string nameErp, string? displayName, bool active, IReadOnlyList<string> forms, IReadOnlyList<ApiUserIp> ips,
            CancellationToken cancellationToken = default);
        Task SetApiUserPasswordAsync(int id, string passwordHash, CancellationToken cancellationToken = default);

        /// <summary>Ghi kết quả đẩy xuống dự án: thành công thì synced_version = version đã đẩy</summary>
        Task MarkApiUserSyncAsync(int id, int pushedVersion, string? error, CancellationToken cancellationToken = default);

        // ----- Phiên đăng nhập -----
        Task<SessionRecord?> FindSessionAsync(Guid id, CancellationToken cancellationToken = default);
        Task<SessionRecord?> FindActiveSessionAsync(string accountType, int accountId, CancellationToken cancellationToken = default);

        /// <summary>Tạo phiên mới; false nếu tài khoản đang có phiên hiệu lực khác (chỉ mục duy nhất của DB)</summary>
        Task<bool> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default);

        /// <summary>Thu hồi phiên cũ và tạo phiên mới trong cùng một transaction (người đăng nhập mới đã xác nhận)</summary>
        Task<bool> ReplaceSessionAsync(Guid oldSessionId, SessionRecord session, string reason, CancellationToken cancellationToken = default);
        Task TouchSessionAsync(Guid id, CancellationToken cancellationToken = default);
        Task RevokeSessionAsync(Guid id, string reason, CancellationToken cancellationToken = default);
        Task RevokeAccountSessionsAsync(string accountType, int accountId, string reason, CancellationToken cancellationToken = default);

        Task CreateChallengeAsync(ChallengeRecord challenge, CancellationToken cancellationToken = default);
        Task<ChallengeRecord?> FindChallengeAsync(Guid id, CancellationToken cancellationToken = default);
        Task<ChallengeRecord?> FindOpenChallengeForSessionAsync(Guid oldSessionId, DateTime now, CancellationToken cancellationToken = default);

        /// <summary>Đóng challenge (accepted | cancelled | expired); false nếu đã đóng trước đó</summary>
        Task<bool> CloseChallengeAsync(Guid id, string outcome, CancellationToken cancellationToken = default);

        // ----- License -----
        Task AddLicenseAsync(LicenseRecord license, string requestSha256, int issuedBy, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<LicenseRecord>> ListLicensesAsync(int? projectId, CancellationToken cancellationToken = default);
        Task<bool> RevokeLicenseAsync(Guid id, int? projectId, string reason, CancellationToken cancellationToken = default);

        // ----- Nhật ký -----
        Task WriteAuditAsync(AuditRecord record, CancellationToken cancellationToken = default);
        Task<AuditPage> ListAuditAsync(AuditQuery query, int page, int pageSize, CancellationToken cancellationToken = default);

        // ----- Form: revision, package, nhật ký đồng bộ (sql/portal/002) -----
        Task<IReadOnlyList<FormSummary>> ListFormsAsync(int projectId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<RevisionInfo>> GetRevisionsAsync(int projectId, string form, CancellationToken cancellationToken = default);
        Task<RevisionContent?> GetRevisionAsync(int projectId, string form, int revision, CancellationToken cancellationToken = default);
        Task<int?> AddRevisionAsync(int projectId, string form, int? baseRevision, string content, string sha256, string status, int createdBy, CancellationToken cancellationToken = default);
        Task<bool> PublishRevisionAsync(int projectId, string form, int revision, int publishedBy, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ActiveForm>> GetActiveFormsAsync(int projectId, CancellationToken cancellationToken = default);
        Task<int> CreatePackageAsync(int projectId, int createdBy, Func<int, PackageDraft> build, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(int projectId, CancellationToken cancellationToken = default);
        Task<PackageRecord?> GetPackageAsync(int projectId, int packageId, CancellationToken cancellationToken = default);
        Task AddSyncLogAsync(SyncLogRecord record, CancellationToken cancellationToken = default);
        Task<SyncLogPage> ListSyncLogAsync(int projectId, int page, int pageSize, CancellationToken cancellationToken = default);
    }

    public sealed record FormSummary(string Name, int LatestRevision, string LatestStatus, int? ActiveRevision, DateTime UpdatedAt, string? UpdatedBy);

    public sealed record RevisionInfo(int Id, int Revision, int? BaseRevision, string Status, string Sha256, DateTime CreatedAt, string? CreatedBy, DateTime? PublishedAt, string? PublishedBy);

    public sealed record RevisionContent(RevisionInfo Info, string Content);

    /// <summary>Revision đang có hiệu lực của một form = revision publish có số lớn nhất</summary>
    public sealed record ActiveForm(string Name, int RevisionId, int Revision, string Content);

    public sealed record PackageInfo(int Id, int ProjectId, DateTime CreatedAt, string? CreatedBy, string ManifestSha256, int FormCount);

    public sealed record PackageItem(string Form, int Revision, string Content);

    public sealed record PackageRecord(PackageInfo Info, string ManifestJson, IReadOnlyList<PackageItem> Items);

    /// <summary>Kết quả hàm dựng gói chạy bên trong transaction của kho (có id gói để ghi vào manifest)</summary>
    public sealed record PackageDraft(string ManifestJson, string ManifestSha256, IReadOnlyList<int> RevisionIds);

    public sealed record SyncLogRecord(int ProjectId, string Env, string Event, int? PackageId, string Source, string? Actor, string Outcome, string? Detail);

    public sealed record SyncLogEntry(long Id, DateTime At, string Env, string Event, int? PackageId, string Source, string? Actor, string Outcome, string? Detail);

    public sealed record SyncLogPage(int Total, IReadOnlyList<SyncLogEntry> Items);
}
