using System.Text.RegularExpressions;
using Models;
using Security;

namespace ApiDocs.ControlPlane
{
    public sealed record OpResult(string Code, object? Data = null)
    {
        public bool Success => Code == "SUCCESS";
    }

    public sealed record MembershipView(int Id, int? ProjectId, string? ProjectCode, string RoleCode, string? ProjectUserName, bool CanRemove);

    public sealed record CreateOptions(IReadOnlyList<ProjectInfo> Projects, bool CanCreateStaff);

    public sealed record AccountDetail(AccountSummary Account, IReadOnlyList<MembershipView> Memberships, bool CanLock, IReadOnlyList<ProjectInfo> AssignableProjects, bool CanAssignStaffRoles);

    /// <summary>
    /// Quản trị tài khoản / membership / dự án trên /Admin. Phân quyền theo phạm vi, kiểm lại ở server cho MỌI thao tác:
    /// administrator trung tâm (membership "mọi dự án") quản lý tất cả; người có quyền users.* ở một dự án chỉ quản lý
    /// KHÁCH HÀNG nằm gọn trong các dự án đó, không động tới staff, không cấp vai trò staff hay quyền mọi dự án.
    /// Không tự khóa mình, không khóa / gỡ administrator trung tâm cuối cùng. Mọi thao tác (kể cả bị từ chối) ghi audit, không ghi mật khẩu
    /// </summary>
    public sealed partial class AccountAdminService(IControlPlaneStore store, ProjectAccessService access, TimeProvider? clock = null)
    {
        private readonly TimeProvider _time = clock ?? TimeProvider.System;
        private readonly Dictionary<int, (int Failures, DateTimeOffset First, DateTimeOffset LockedUntil)> _passwordAttempts = [];
        private readonly object _gate = new();
        public const int MaxPasswordFailures = 5;
        public static readonly TimeSpan PasswordLockFor = TimeSpan.FromMinutes(15);

        public const int MinPasswordLength = 12;
        public const int MaxPageSize = 50;

        [GeneratedRegex("^[A-Za-z0-9._@-]{3,64}$")] private static partial Regex LoginPattern();
        [GeneratedRegex("^[A-Za-z0-9._@-]{1,32}$")] private static partial Regex ApiUserPattern();
        [GeneratedRegex("^[A-Za-z0-9_-]{2,32}$")] private static partial Regex ProjectCodePattern();

        // ---------- Danh sách / chi tiết ----------
        public async Task<OpResult> ListAccountsAsync(int actorId, string? search, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor == null)
            {
                return new OpResult("FORBIDDEN");
            }

            IReadOnlyCollection<int>? scope = ReadScope(actor);
            if (scope is { Count: 0 })
            {
                await Audit(actorId, null, "accounts.list", "*", "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }

            string text = (search ?? "").Trim();
            return new OpResult("SUCCESS", await store.ListAccountsAsync(text.Length > 64 ? text[..64] : text, Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), scope, cancellationToken));
        }

        public async Task<OpResult> GetDetailAsync(int actorId, int targetId, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            PortalAccount? target = actor == null ? null : await store.FindAccountAsync(targetId, cancellationToken);
            if (actor == null || target == null)
            {
                return new OpResult("NOT_FOUND");
            }

            IReadOnlyList<MembershipRow> rows = await store.GetMembershipsAsync(targetId, cancellationToken);
            IReadOnlyCollection<int>? scope = ReadScope(actor);
            // Người chỉ có quyền ở vài dự án chỉ thấy tài khoản có membership ở các dự án đó (ngoài phạm vi = không tồn tại, tránh dò ID)
            if (scope != null && !rows.Any(r => r.ProjectId is int p && scope.Contains(p)))
            {
                return new OpResult("NOT_FOUND");
            }

            IReadOnlyList<ProjectInfo> projects = await store.GetActiveProjectsAsync(cancellationToken);
            List<MembershipView> views = rows
                .Where(r => scope == null || r.ProjectId is int p && scope.Contains(p))
                .Select(r => new MembershipView(r.Id, r.ProjectId, projects.FirstOrDefault(p => p.Id == r.ProjectId)?.Code, r.RoleCode, r.ProjectUserName, CanChangeMemberships(actor, target, r.ProjectId)))
                .ToList();

            bool central = actor.CanCentral(PortalPermissions.UsersRolesManage);
            IReadOnlyList<ProjectInfo> assignable = target.Kind == AccountKind.Customer || central
                ? projects.Where(p => CanChangeMemberships(actor, target, p.Id)).ToList()
                : [];

            return new OpResult("SUCCESS", new AccountDetail(new AccountSummary(target.Id, target.Login, target.DisplayName, target.Kind, target.Active), views,
                actor.Account.Id != target.Id && CanManage(actor, target, rows, PortalPermissions.UsersDeactivate), assignable, central && target.Kind == AccountKind.Staff));
        }

        /// <summary>Lựa chọn cho form tạo tài khoản theo quyền của người thao tác (dự án được phép, có tạo được staff hay không)</summary>
        public async Task<OpResult> GetCreateOptionsAsync(int actorId, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor == null)
            {
                return new OpResult("FORBIDDEN");
            }
            IReadOnlyList<ProjectInfo> projects = await store.GetActiveProjectsAsync(cancellationToken);
            bool central = actor.CanCentral(PortalPermissions.UsersCreate);
            List<ProjectInfo> allowed = projects.Where(p => central || actor.Can(p.Id, PortalPermissions.UsersCreate)).ToList();
            bool staff = central && actor.CanCentral(PortalPermissions.UsersRolesManage);
            return allowed.Count == 0 && !staff ? new OpResult("FORBIDDEN") : new OpResult("SUCCESS", new CreateOptions(allowed, staff));
        }

        // ---------- Tạo tài khoản ----------
        public async Task<OpResult> CreateAccountAsync(int actorId, string? login, string? displayName, string? kind, string? password, int? projectId, string? roleCode, string? projectUser,
            CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor == null)
            {
                return new OpResult("FORBIDDEN");
            }

            login = (login ?? "").Trim();
            displayName = (displayName ?? "").Trim();
            projectUser = string.IsNullOrWhiteSpace(projectUser) ? null : projectUser.Trim();
            if (!LoginPattern().IsMatch(login) || displayName.Length is 0 or > 100 || kind is not (AccountKind.Staff or AccountKind.Customer)
                || password == null || password.Length < MinPasswordLength || password.Length > 256 || roleCode is not (RoleCodes.Partner or RoleCodes.Deployment or RoleCodes.Administrator)
                || projectUser != null && (!ApiUserPattern().IsMatch(projectUser) || kind != AccountKind.Customer))
            {
                return new OpResult("INVALID_INPUT");
            }

            string roleScope = roleCode == RoleCodes.Partner ? AccountKind.Customer : AccountKind.Staff;
            if (!PortalMembershipRules.IsValid(kind, roleScope, roleCode, projectId != null))
            {
                await Audit(actorId, projectId, "account.create", login, "REJECTED", "membership invalid", cancellationToken);
                return new OpResult("INVALID_MEMBERSHIP");
            }

            bool allowed = kind == AccountKind.Customer
                ? projectId is int p && (actor.Can(p, PortalPermissions.UsersCreate) || actor.CanCentral(PortalPermissions.UsersCreate))
                : actor.CanCentral(PortalPermissions.UsersCreate) && actor.CanCentral(PortalPermissions.UsersRolesManage);
            if (!allowed)
            {
                await Audit(actorId, projectId, "account.create", login, "DENIED", $"kind={kind}", cancellationToken);
                return new OpResult("FORBIDDEN");
            }

            if (projectId != null && !(await store.GetActiveProjectsAsync(cancellationToken)).Any(p => p.Id == projectId))
            {
                return new OpResult("INVALID_MEMBERSHIP");
            }

            int? id = await store.CreateAccountAsync(login, displayName, kind, PortalPasswordHasher.Hash(password), new NewMembership(projectId, roleCode, projectUser), actorId, cancellationToken);
            if (id == null)
            {
                await Audit(actorId, projectId, "account.create", login, "REJECTED", "duplicate login", cancellationToken);
                return new OpResult("DUPLICATE");
            }

            await Audit(actorId, projectId, "account.create", login, "SUCCESS", $"kind={kind}, role={roleCode}", cancellationToken);
            return new OpResult("SUCCESS", id);
        }

        // ---------- Khóa / mở ----------
        public async Task<OpResult> SetActiveAsync(int actorId, int targetId, bool active, CancellationToken cancellationToken = default)
        {
            string action = active ? "account.unlock" : "account.lock";
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            PortalAccount? target = actor == null ? null : await store.FindAccountAsync(targetId, cancellationToken);
            if (actor == null || target == null)
            {
                return new OpResult("NOT_FOUND");
            }

            IReadOnlyList<MembershipRow> rows = await store.GetMembershipsAsync(targetId, cancellationToken);
            if (!CanManage(actor, target, rows, PortalPermissions.UsersDeactivate))
            {
                await AuditTarget(actorId, rows, action, target.Login, "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }
            if (!active && actorId == targetId)
            {
                await AuditTarget(actorId, rows, action, target.Login, "REJECTED", "self", cancellationToken);
                return new OpResult("SELF_LOCK");
            }
            // Việc "còn ít nhất một administrator trung tâm" kiểm và ghi trong cùng transaction của kho (không có khoảng hở giữa hai người cùng khóa nhau)
            if (!await store.SetAccountActiveAsync(targetId, active, keepOneCentralAdministrator: !active && IsCentralAdministrator(target, rows), cancellationToken))
            {
                await AuditTarget(actorId, rows, action, target.Login, "REJECTED", "last central administrator", cancellationToken);
                return new OpResult("LAST_ADMIN");
            }

            await AuditTarget(actorId, rows, action, target.Login, "SUCCESS", null, cancellationToken);
            return new OpResult("SUCCESS");
        }

        // ---------- Membership ----------
        public async Task<OpResult> AddMembershipAsync(int actorId, int targetId, int? projectId, string? roleCode, string? projectUser, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            PortalAccount? target = actor == null ? null : await store.FindAccountAsync(targetId, cancellationToken);
            if (actor == null || target == null)
            {
                return new OpResult("NOT_FOUND");
            }

            projectUser = string.IsNullOrWhiteSpace(projectUser) ? null : projectUser.Trim();
            if (roleCode is not (RoleCodes.Partner or RoleCodes.Deployment or RoleCodes.Administrator) || projectUser != null && (!ApiUserPattern().IsMatch(projectUser) || target.Kind != AccountKind.Customer))
            {
                return new OpResult("INVALID_INPUT");
            }
            string roleScope = roleCode == RoleCodes.Partner ? AccountKind.Customer : AccountKind.Staff;
            if (!PortalMembershipRules.IsValid(target.Kind, roleScope, roleCode, projectId != null))
            {
                await Audit(actorId, projectId, "membership.add", target.Login, "REJECTED", $"role={roleCode}", cancellationToken);
                return new OpResult("INVALID_MEMBERSHIP");
            }
            if (!CanChangeMemberships(actor, target, projectId))
            {
                await Audit(actorId, projectId, "membership.add", target.Login, "DENIED", $"role={roleCode}", cancellationToken);
                return new OpResult("FORBIDDEN");
            }
            if (projectId != null && !(await store.GetActiveProjectsAsync(cancellationToken)).Any(p => p.Id == projectId))
            {
                return new OpResult("INVALID_MEMBERSHIP");
            }

            if (!await store.AddMembershipAsync(targetId, new NewMembership(projectId, roleCode, projectUser), actorId, cancellationToken))
            {
                await Audit(actorId, projectId, "membership.add", target.Login, "REJECTED", "duplicate", cancellationToken);
                return new OpResult("DUPLICATE");
            }
            await Audit(actorId, projectId, "membership.add", target.Login, "SUCCESS", $"role={roleCode}", cancellationToken);
            return new OpResult("SUCCESS");
        }

        public async Task<OpResult> RemoveMembershipAsync(int actorId, int membershipId, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            MembershipRow? membership = actor == null ? null : await store.GetMembershipAsync(membershipId, cancellationToken);
            PortalAccount? target = membership == null ? null : await store.FindAccountAsync(membership.AccountId, cancellationToken);
            if (actor == null || membership == null || target == null)
            {
                return new OpResult("NOT_FOUND");
            }

            if (!CanChangeMemberships(actor, target, membership.ProjectId))
            {
                await Audit(actorId, membership.ProjectId, "membership.remove", target.Login, "DENIED", $"role={membership.RoleCode}", cancellationToken);
                return new OpResult("FORBIDDEN");
            }
            bool centralAdministrator = target.Active && membership.ProjectId == null && membership.RoleCode == RoleCodes.Administrator
                && PortalMembershipRules.IsValid(target.Kind, membership.RoleScope, membership.RoleCode, false);
            if (!await store.RemoveMembershipAsync(membershipId, keepOneCentralAdministrator: centralAdministrator, cancellationToken))
            {
                await Audit(actorId, null, "membership.remove", target.Login, "REJECTED", "last central administrator", cancellationToken);
                return new OpResult("LAST_ADMIN");
            }

            await Audit(actorId, membership.ProjectId, "membership.remove", target.Login, "SUCCESS", $"role={membership.RoleCode}", cancellationToken);
            return new OpResult("SUCCESS");
        }

        // ---------- Mật khẩu của chính mình ----------
        public async Task<OpResult> ChangeOwnPasswordAsync(int accountId, string? current, string? replacement, CancellationToken cancellationToken = default)
        {
            PortalAccount? account = await store.FindAccountAsync(accountId, cancellationToken);
            if (account is not { Active: true })
            {
                return new OpResult("NOT_FOUND");
            }
            // Người giữ phiên đánh cắp không được thử mật khẩu hiện tại vô hạn: khóa việc đổi mật khẩu sau vài lần sai (chưa tốn công băm)
            if (IsPasswordLocked(accountId))
            {
                await Audit(accountId, null, "password.change", account.Login, "DENIED", "too many attempts", cancellationToken);
                return new OpResult("TOO_MANY_ATTEMPTS");
            }
            if (string.IsNullOrEmpty(current) || !PortalPasswordHasher.Verify(current, account.PasswordHash))
            {
                RecordPasswordFailure(accountId);
                await Audit(accountId, null, "password.change", account.Login, "DENIED", "wrong current", cancellationToken);
                return new OpResult("INVALID_CREDENTIAL");
            }
            ClearPasswordFailures(accountId);
            if (replacement == null || replacement.Length < MinPasswordLength || replacement.Length > 256 || replacement == current)
            {
                return new OpResult("INVALID_INPUT");
            }

            await store.UpdatePasswordHashAsync(accountId, PortalPasswordHasher.Hash(replacement), cancellationToken);
            await Audit(accountId, null, "password.change", account.Login, "SUCCESS", null, cancellationToken);
            return new OpResult("SUCCESS");
        }

        private bool IsPasswordLocked(int accountId)
        {
            lock (_gate)
            {
                return _passwordAttempts.TryGetValue(accountId, out var state) && state.LockedUntil > _time.GetUtcNow();
            }
        }

        private void RecordPasswordFailure(int accountId)
        {
            lock (_gate)
            {
                DateTimeOffset now = _time.GetUtcNow();
                bool fresh = !_passwordAttempts.TryGetValue(accountId, out var state) || now - state.First > PasswordLockFor && state.LockedUntil <= now;
                int failures = fresh ? 1 : state.Failures + 1;
                _passwordAttempts[accountId] = (failures, fresh ? now : state.First, failures >= MaxPasswordFailures ? now + PasswordLockFor : fresh ? default : state.LockedUntil);
            }
        }

        private void ClearPasswordFailures(int accountId)
        {
            lock (_gate)
            {
                _passwordAttempts.Remove(accountId);
            }
        }

        // ---------- Dự án (chỉ administrator trung tâm có settings.manage) ----------
        public async Task<OpResult> ListProjectsAsync(int actorId, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor == null || !actor.CanCentral(PortalPermissions.SettingsManage))
            {
                await Audit(actorId, null, "projects.list", "*", "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }
            return new OpResult("SUCCESS", await store.GetAllProjectsAsync(cancellationToken));
        }

        public async Task<OpResult> CreateProjectAsync(int actorId, string? code, string? name, string? apiBaseUrl, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor == null || !actor.CanCentral(PortalPermissions.SettingsManage))
            {
                await Audit(actorId, null, "project.create", code, "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }

            code = (code ?? "").Trim();
            name = (name ?? "").Trim();
            string? url = NormalizeApiUrl(apiBaseUrl);
            if (!ProjectCodePattern().IsMatch(code) || name.Length is 0 or > 100 || url == null)
            {
                return new OpResult("INVALID_INPUT");
            }

            int? id = await store.CreateProjectAsync(code, name, url, cancellationToken);
            if (id == null)
            {
                await Audit(actorId, null, "project.create", code, "REJECTED", "duplicate code", cancellationToken);
                return new OpResult("DUPLICATE");
            }
            await Audit(actorId, id, "project.create", code, "SUCCESS", null, cancellationToken);
            return new OpResult("SUCCESS", id);
        }

        public async Task<OpResult> UpdateProjectAsync(int actorId, int projectId, string? name, string? apiBaseUrl, bool active, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor == null || !actor.CanCentral(PortalPermissions.SettingsManage))
            {
                await Audit(actorId, projectId, "project.update", null, "DENIED", null, cancellationToken);
                return new OpResult("FORBIDDEN");
            }

            name = (name ?? "").Trim();
            string? url = NormalizeApiUrl(apiBaseUrl);
            if (name.Length is 0 or > 100 || url == null)
            {
                return new OpResult("INVALID_INPUT");
            }
            if (!await store.UpdateProjectAsync(projectId, name, url, active, cancellationToken))
            {
                return new OpResult("NOT_FOUND");
            }
            await Audit(actorId, projectId, active ? "project.update" : "project.deactivate", name, "SUCCESS", null, cancellationToken);
            return new OpResult("SUCCESS");
        }

        /// <summary>
        /// Địa chỉ Api dự án: tuyệt đối, https (http chỉ cho localhost khi phát triển), không user:pass, không query/fragment.
        /// Trả dạng chuẩn hóa (bỏ "/" cuối); null nếu không hợp lệ. Đây là danh sách duy nhất mà portal được phép gọi tới (chống SSRF)
        /// </summary>
        public static string? NormalizeApiUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
            {
                return null;
            }
            bool local = uri.IsLoopback;
            if (!(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && local) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0)
            {
                return null;
            }
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        // ---------- Quy tắc phạm vi ----------
        /// <summary>Null = thấy mọi tài khoản (administrator trung tâm có users.read); ngược lại là các dự án có users.read (rỗng = không có quyền)</summary>
        private static IReadOnlyCollection<int>? ReadScope(AccountAccess actor) =>
            actor.CanCentral(PortalPermissions.UsersRead) ? null : actor.Projects.Where(p => p.Permissions.Contains(PortalPermissions.UsersRead)).Select(p => p.Project.Id).ToList();

        /// <summary>
        /// Quản lý (khóa/mở...) tài khoản đích: administrator trung tâm có quyền đó thì được; ngược lại chỉ khi đích là khách hàng
        /// và MỌI membership của nó nằm trong các dự án mà người thao tác có quyền đó
        /// </summary>
        private static bool CanManage(AccountAccess actor, PortalAccount target, IReadOnlyList<MembershipRow> rows, string permission)
        {
            if (actor.CanCentral(permission))
            {
                return true;
            }
            return target.Kind == AccountKind.Customer && rows.Count > 0 && rows.All(r => r.ProjectId is int p && actor.Can(p, permission));
        }

        /// <summary>Gán / gỡ membership: khách thì cần users.roles.manage ở dự án đó (hoặc trung tâm); staff thì chỉ administrator trung tâm</summary>
        private static bool CanChangeMemberships(AccountAccess actor, PortalAccount target, int? projectId)
        {
            if (actor.CanCentral(PortalPermissions.UsersRolesManage))
            {
                return true;
            }
            return target.Kind == AccountKind.Customer && projectId is int p && actor.Can(p, PortalPermissions.UsersRolesManage);
        }

        private static bool IsCentralAdministrator(PortalAccount account, IReadOnlyList<MembershipRow> rows) =>
            account.Kind == AccountKind.Staff && rows.Any(r => r.ProjectId == null && r.RoleCode == RoleCodes.Administrator && PortalMembershipRules.IsValid(account.Kind, r.RoleScope, r.RoleCode, false));

        /// <summary>
        /// Audit về một tài khoản đích gắn với MỌI dự án của nó (một dòng mỗi dự án) để người chỉ có audit.read ở dự án thấy được
        /// sự kiện của khách thuộc dự án mình; tài khoản không thuộc dự án nào (administrator trung tâm) thì một dòng không dự án
        /// </summary>
        private async Task AuditTarget(int actorId, IReadOnlyList<MembershipRow> rows, string action, string target, string outcome, string? detail, CancellationToken cancellationToken)
        {
            List<int> projects = rows.Where(r => r.ProjectId != null).Select(r => r.ProjectId!.Value).Distinct().ToList();
            if (projects.Count == 0)
            {
                await Audit(actorId, null, action, target, outcome, detail, cancellationToken);
                return;
            }
            foreach (int project in projects)
            {
                await Audit(actorId, project, action, target, outcome, detail, cancellationToken);
            }
        }

        private Task Audit(int actorId, int? projectId, string action, string? target, string outcome, string? detail, CancellationToken cancellationToken) =>
            store.WriteAuditAsync(new AuditRecord(actorId, projectId, action, target, outcome, detail), cancellationToken);
    }
}
