using Models;

namespace WebPortal.ControlPlane
{
    public static class AccountKind
    {
        public const string Staff = "staff";
        public const string Customer = "customer";
    }

    public static class RoleCodes
    {
        public const string Partner = "partner";
        public const string Deployment = "deployment";
        public const string Administrator = "administrator";
    }

    public sealed record PortalAccount(int Id, string Login, string DisplayName, string Kind, bool Active, string PasswordHash);

    public sealed record ProjectInfo(int Id, string Code, string Name);

    /// <param name="ProjectId">Null = mọi dự án (chỉ hợp lệ cho staff có vai trò administrator)</param>
    public sealed record MembershipRow(int Id, int AccountId, int? ProjectId, string RoleCode, string RoleScope, string? ProjectUserName);

    public sealed record ProjectDetail(int Id, string Code, string Name, string ApiBaseUrl, bool Active);

    /// <summary>Tài khoản dùng cho giao diện: KHÔNG có băm mật khẩu</summary>
    public sealed record AccountSummary(int Id, string Login, string DisplayName, string Kind, bool Active);

    public sealed record AccountPage(int Total, IReadOnlyList<AccountSummary> Items);

    public sealed record NewMembership(int? ProjectId, string RoleCode, string? ProjectUserName);

    public sealed record AuditRecord(int? ActorAccountId, int? ProjectId, string Action, string? Target, string Outcome, string? Detail = null);

    /// <summary>Cổng lưu trữ của control plane (DB portal riêng: sql/portal/*)</summary>
    public interface IControlPlaneStore
    {
        Task<PortalAccount?> FindAccountByLoginAsync(string login, CancellationToken cancellationToken = default);

        Task<PortalAccount?> FindAccountAsync(int id, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<MembershipRow>> GetMembershipsAsync(int accountId, CancellationToken cancellationToken = default);

        /// <summary>Dự án còn hoạt động</summary>
        Task<IReadOnlyList<ProjectInfo>> GetActiveProjectsAsync(CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> GetRolePermissionsAsync(string roleCode, CancellationToken cancellationToken = default);

        Task WriteAuditAsync(AuditRecord record, CancellationToken cancellationToken = default);

        // ----- Quản trị tài khoản / dự án -----
        /// <param name="projectIds">Null = mọi tài khoản; có giá trị = chỉ tài khoản có membership ở các dự án này</param>
        Task<AccountPage> ListAccountsAsync(string search, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default);

        /// <summary>Tạo tài khoản (kèm membership đầu) trong một transaction; null nếu trùng tên đăng nhập hoặc vai trò không tồn tại</summary>
        Task<int?> CreateAccountAsync(string login, string displayName, string kind, string passwordHash, NewMembership first, int? grantedBy, CancellationToken cancellationToken = default);

        /// <param name="keepOneCentralAdministrator">true: chỉ thực hiện nếu sau đó vẫn còn ít nhất một administrator trung tâm đang hoạt động khác tài khoản này;
        /// kiểm và ghi trong CÙNG một transaction nên hai người thao tác đồng thời không cùng vượt qua được</param>
        /// <returns>false nếu bị chặn bởi quy tắc trên</returns>
        Task<bool> SetAccountActiveAsync(int id, bool active, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default);

        Task UpdatePasswordHashAsync(int id, string passwordHash, CancellationToken cancellationToken = default);

        Task<MembershipRow?> GetMembershipAsync(int membershipId, CancellationToken cancellationToken = default);

        /// <summary>False nếu đã có membership y hệt hoặc vai trò không tồn tại</summary>
        Task<bool> AddMembershipAsync(int accountId, NewMembership membership, int? grantedBy, CancellationToken cancellationToken = default);

        /// <param name="keepOneCentralAdministrator">Như SetAccountActiveAsync: gỡ membership administrator trung tâm chỉ khi còn administrator khác</param>
        Task<bool> RemoveMembershipAsync(int membershipId, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ProjectDetail>> GetAllProjectsAsync(CancellationToken cancellationToken = default);

        /// <summary>Null nếu trùng mã dự án</summary>
        Task<int?> CreateProjectAsync(string code, string name, string apiBaseUrl, CancellationToken cancellationToken = default);

        Task<bool> UpdateProjectAsync(int id, string name, string apiBaseUrl, bool active, CancellationToken cancellationToken = default);

        // ----- Audit và số liệu tổng quan (chỉ đọc) -----
        /// <param name="projectIds">Null = mọi dòng audit (kể cả không thuộc dự án); có giá trị = chỉ dòng của các dự án này</param>
        Task<AuditPage> ListAuditAsync(AuditQuery query, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default);

        /// <summary>Số tài khoản theo loại/trạng thái; projectIds có giá trị = chỉ tài khoản có membership ở các dự án đó</summary>
        Task<AccountCounts> GetAccountCountsAsync(IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default);

        /// <summary>Thống kê từng dự án (kể cả đang ngừng); null = mọi dự án</summary>
        Task<IReadOnlyList<ProjectStat>> GetProjectStatsAsync(IReadOnlyCollection<int>? projectIds, DateTime deniedSince, CancellationToken cancellationToken = default);

        /// <summary>Đếm dòng audit từ mốc since theo kết quả (và tiền tố hành động nếu có), cùng quy tắc phạm vi của ListAuditAsync</summary>
        Task<int> CountAuditAsync(DateTime since, string outcome, string? actionPrefix, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default);

        // ----- Form: revision, package, nhật ký đồng bộ (sql/portal/002) -----
        Task<IReadOnlyList<FormSummary>> ListFormsAsync(int projectId, CancellationToken cancellationToken = default);

        /// <summary>Mới nhất trước, không kèm nội dung</summary>
        Task<IReadOnlyList<RevisionInfo>> GetRevisionsAsync(int projectId, string form, CancellationToken cancellationToken = default);

        Task<RevisionContent?> GetRevisionAsync(int projectId, string form, int revision, CancellationToken cancellationToken = default);

        /// <summary>
        /// Thêm revision mới trong một transaction có khóa. Khóa lạc quan: baseRevision phải đúng bằng revision lớn nhất hiện có
        /// (null nếu form chưa có revision nào); lệch thì trả null (CONFLICT), không ghi gì
        /// </summary>
        Task<int?> AddRevisionAsync(int projectId, string form, int? baseRevision, string content, string sha256, string status, int createdBy, CancellationToken cancellationToken = default);

        /// <summary>validated → published; false nếu revision không tồn tại hoặc không ở trạng thái validated</summary>
        Task<bool> PublishRevisionAsync(int projectId, string form, int revision, int publishedBy, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ActiveForm>> GetActiveFormsAsync(int projectId, CancellationToken cancellationToken = default);

        /// <summary>Tạo gói: chèn dòng gói, gọi build(id) để dựng manifest, lưu manifest + danh sách revision, tất cả trong một transaction</summary>
        Task<int> CreatePackageAsync(int projectId, int createdBy, Func<int, PackageDraft> build, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(int projectId, CancellationToken cancellationToken = default);

        Task<PackageRecord?> GetPackageAsync(int projectId, int packageId, CancellationToken cancellationToken = default);

        Task AddSyncLogAsync(SyncLogRecord record, CancellationToken cancellationToken = default);

        /// <param name="projectIds">Chỉ các dự án này (người xem có quyền); projectId thu hẹp thêm nếu có</param>
        Task<SyncLogPage> ListSyncLogAsync(IReadOnlyCollection<int> projectIds, int? projectId, string env, string outcome, int page, int pageSize, CancellationToken cancellationToken = default);
    }

    public sealed record ProjectGrant(ProjectInfo Project, IReadOnlySet<string> Roles, IReadOnlySet<string> Permissions, string? ProjectUserName);

    /// <summary>Quyền hiệu lực của một tài khoản theo từng dự án, tính ở server mỗi request (không nằm trong cookie)</summary>
    /// <param name="CentralPermissions">Quyền từ membership "mọi dự án" (administrator trung tâm), dùng cho thao tác không thuộc một dự án cụ thể</param>
    public sealed record AccountAccess(PortalAccount Account, IReadOnlyList<ProjectGrant> Projects, IReadOnlySet<string> CentralPermissions)
    {
        public bool IsCentral => CentralPermissions.Count > 0;

        public bool CanCentral(string permission) => CentralPermissions.Contains(permission);

        public ProjectGrant? For(int projectId) => Projects.FirstOrDefault(p => p.Project.Id == projectId);

        public bool Can(int projectId, string permission) => For(projectId)?.Permissions.Contains(permission) == true;
    }

    /// <summary>
    /// Tính quyền theo dự án từ tài khoản + membership. Deny mặc định:
    /// khách hàng chỉ có vai trò phạm vi customer và bắt buộc gắn một dự án; staff chỉ có vai trò phạm vi staff;
    /// membership không dự án (mọi dự án) chỉ hợp lệ với vai trò administrator của staff. Dòng sai quy tắc bị bỏ qua
    /// (dù ai đó ghi thẳng vào DB), tài khoản khóa hoặc dự án ngừng thì không có quyền
    /// </summary>
    public sealed class ProjectAccessService(IControlPlaneStore store)
    {
        public async Task<AccountAccess?> GetAccessAsync(int accountId, CancellationToken cancellationToken = default)
        {
            PortalAccount? account = await store.FindAccountAsync(accountId, cancellationToken);
            if (account is not { Active: true })
            {
                return null;
            }

            IReadOnlyList<ProjectInfo> projects = await store.GetActiveProjectsAsync(cancellationToken);
            Dictionary<string, IReadOnlySet<string>> permissionCache = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, (HashSet<string> Roles, HashSet<string> Permissions, string? User)> grants = [];
            HashSet<string> central = new(StringComparer.Ordinal);

            foreach (MembershipRow row in await store.GetMembershipsAsync(accountId, cancellationToken))
            {
                if (!IsValid(account, row))
                {
                    continue;
                }

                if (!permissionCache.TryGetValue(row.RoleCode, out IReadOnlySet<string>? permissions))
                {
                    permissions = new HashSet<string>((await store.GetRolePermissionsAsync(row.RoleCode, cancellationToken)).Where(PortalPermissions.All.Contains), StringComparer.Ordinal);
                    permissionCache[row.RoleCode] = permissions;
                }

                if (row.ProjectId == null)
                {
                    central.UnionWith(permissions);
                }

                foreach (ProjectInfo project in projects.Where(p => row.ProjectId == null || p.Id == row.ProjectId))
                {
                    if (!grants.TryGetValue(project.Id, out var grant))
                    {
                        grant = ([], [], null);
                    }
                    grant.Roles.Add(row.RoleCode);
                    grant.Permissions.UnionWith(permissions);
                    grants[project.Id] = (grant.Roles, grant.Permissions, grant.User ?? row.ProjectUserName);
                }
            }

            return new AccountAccess(account, projects.Where(p => grants.ContainsKey(p.Id))
                .Select(p => new ProjectGrant(p, grants[p.Id].Roles, grants[p.Id].Permissions, grants[p.Id].User)).ToList(), central);
        }

        /// <summary>Kiểm một thao tác; từ chối thì ghi audit. Trả quyền hiệu lực nếu cho phép</summary>
        public async Task<bool> AuthorizeAsync(int accountId, int projectId, string permission, string action, CancellationToken cancellationToken = default)
        {
            AccountAccess? access = await GetAccessAsync(accountId, cancellationToken);
            bool allowed = access?.Can(projectId, permission) == true;
            if (!allowed)
            {
                await store.WriteAuditAsync(new AuditRecord(accountId, projectId, action, permission, "DENIED"), cancellationToken);
            }
            return allowed;
        }

        /// <summary>Quy tắc hợp lệ của một membership (dùng khi đọc và khi tạo/sửa membership sau này)</summary>
        public static bool IsValid(PortalAccount account, MembershipRow row)
        {
            return PortalMembershipRules.IsValid(account.Kind, row.RoleScope, row.RoleCode, row.ProjectId != null);
        }
    }

    public sealed record LoginResult(string Code, PortalAccount? Account = null)
    {
        public bool Success => Code == "SUCCESS";
    }

    /// <summary>
    /// Đăng nhập control plane: không lộ tài khoản có tồn tại hay không (cùng mã lỗi, cùng thời gian xử lý, tên lạ cũng bị đếm).
    /// Hai lớp hạn chế để kẻ tấn công KHÔNG khóa được tài khoản của người khác từ một địa chỉ IP:
    /// khóa theo cặp (tên đăng nhập, IP) sau MaxFailures lần sai, và khóa theo tên đăng nhập chỉ khi tổng lần sai (từ mọi IP) vượt GlobalMaxFailures.
    /// Bộ đếm nằm trong bộ nhớ một tiến trình (khởi động lại thì đếm lại); mật khẩu băm PBKDF2
    /// </summary>
    public sealed class AccountLoginService(IControlPlaneStore store, TimeProvider time)
    {
        public const int MaxFailures = 5;
        public const int GlobalMaxFailures = 30;
        public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
        private const int MaxTracked = 20000;

        private sealed record Attempt(int Failures, DateTimeOffset First, DateTimeOffset LockedUntil);

        private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        /// <summary>Dấu vết mật khẩu hiện tại trong cookie: đổi mật khẩu thì mọi phiên cũ mất hiệu lực (không chứa băm, chỉ một đoạn băm của băm)</summary>
        public static string StampOf(PortalAccount account) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.PasswordHash)))[..16];

        public async Task<LoginResult> LoginAsync(string? login, string? password, string? ipAddress = null, CancellationToken cancellationToken = default)
        {
            login = (login ?? "").Trim();
            if (login.Length is 0 or > 64 || string.IsNullOrEmpty(password) || password.Length > 256)
            {
                return new LoginResult("INVALID_CREDENTIAL");
            }
            string pairKey = "p:" + login + "|" + (ipAddress ?? ""), loginKey = "l:" + login;
            if (IsLocked(pairKey) || IsLocked(loginKey))
            {
                return new LoginResult("ACCOUNT_LOCKED");
            }

            PortalAccount? account = await store.FindAccountByLoginAsync(login, cancellationToken);
            bool ok;
            if (account is { Active: true })
            {
                ok = Security.PortalPasswordHasher.Verify(password, account.PasswordHash);
            }
            else
            {
                Security.PortalPasswordHasher.BurnTime(password);
                ok = false;
            }

            if (!ok)
            {
                RecordFailure(pairKey, MaxFailures);
                RecordFailure(loginKey, GlobalMaxFailures);
                // Tên đăng nhập lạ có thể chứa ký tự điều khiển: bỏ đi trước khi ghi audit để không làm giả dòng nhật ký
                await store.WriteAuditAsync(new AuditRecord(account?.Id, null, "login", NoControl(login), "DENIED"), cancellationToken);
                return new LoginResult("INVALID_CREDENTIAL");
            }

            Reset(pairKey);
            await store.WriteAuditAsync(new AuditRecord(account!.Id, null, "login", account.Login, "SUCCESS"), cancellationToken);
            return new LoginResult("SUCCESS", account);
        }

        public static string NoControl(string value) => new(value.Where(c => !char.IsControl(c)).ToArray());

        private bool IsLocked(string key)
        {
            lock (_gate)
            {
                return _attempts.TryGetValue(key, out Attempt? state) && state.LockedUntil > time.GetUtcNow();
            }
        }

        private void RecordFailure(string key, int threshold)
        {
            lock (_gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                if (_attempts.Count >= MaxTracked)
                {
                    foreach (string stale in _attempts.Where(a => a.Value.LockedUntil <= now && now - a.Value.First > LockFor).Select(a => a.Key).ToList())
                    {
                        _attempts.Remove(stale);
                    }
                }

                // Cửa sổ đếm 15 phút kể từ lần sai đầu; hết cửa sổ hoặc hết khóa thì đếm lại từ đầu
                bool fresh = !_attempts.TryGetValue(key, out Attempt? state) || now - state.First > LockFor && state.LockedUntil <= now;
                int failures = fresh ? 1 : state!.Failures + 1;
                _attempts[key] = new Attempt(failures, fresh ? now : state!.First, failures >= threshold ? now + LockFor : fresh ? default : state!.LockedUntil);
            }
        }

        private void Reset(string key)
        {
            lock (_gate)
            {
                _attempts.Remove(key);
            }
        }
    }
}
