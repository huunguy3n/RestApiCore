using WebPortal.ControlPlane;

namespace Tests
{
    /// <summary>Kho control plane trong bộ nhớ cho test, mô phỏng đúng các ràng buộc của DB portal (trùng login, trùng membership, vai trò phải tồn tại)</summary>
    internal sealed partial class ControlPlaneFakeStore : IControlPlaneStore
    {
        private int _nextAccount = 1000, _nextMembership = 5000, _nextProject = 100;

        public Dictionary<int, PortalAccount> Accounts { get; } = [];
        public Dictionary<int, IReadOnlyList<MembershipRow>> Memberships { get; } = [];
        public IReadOnlyList<ProjectInfo> Projects { get; set; } = [];
        public List<ProjectDetail> AllProjects { get; } = [];
        public Dictionary<string, IReadOnlyList<string>> Permissions { get; } = [];
        public List<AuditRecord> Audit { get; } = [];

        private bool ApiUserTaken(NewMembership m) =>
            m.ProjectUserName != null && Memberships.Values.SelectMany(x => x).Any(r => r.ProjectId == m.ProjectId && string.Equals(r.ProjectUserName, m.ProjectUserName, StringComparison.OrdinalIgnoreCase));

        private static string ScopeOf(string role) => role == RoleCodes.Partner ? AccountKind.Customer : AccountKind.Staff;

        public Task<PortalAccount?> FindAccountByLoginAsync(string login, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accounts.Values.FirstOrDefault(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase)));

        public Task<PortalAccount?> FindAccountAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(Accounts.GetValueOrDefault(id));

        public Task<IReadOnlyList<MembershipRow>> GetMembershipsAsync(int accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.GetValueOrDefault(accountId) ?? []);

        public Task<IReadOnlyList<ProjectInfo>> GetActiveProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Projects);

        public Task<IReadOnlyList<string>> GetRolePermissionsAsync(string roleCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Permissions.GetValueOrDefault(roleCode) ?? []);

        /// <summary>Thời điểm của từng dòng trong Audit (cùng chỉ số); mặc định là giờ hiện tại</summary>
        public List<DateTime> AuditTimes { get; } = [];

        public Task WriteAuditAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            AddAudit(record, DateTime.Now);
            return Task.CompletedTask;
        }

        public void AddAudit(AuditRecord record, DateTime at)
        {
            Audit.Add(record);
            AuditTimes.Add(at);
        }

        private IEnumerable<int> AuditIndexes(IReadOnlyCollection<int>? projectIds) =>
            Enumerable.Range(0, Audit.Count).Where(i => projectIds == null || Audit[i].ProjectId is int p && projectIds.Contains(p));

        public Task<AuditPage> ListAuditAsync(AuditQuery query, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default)
        {
            string? Login(AuditRecord r) => r.ActorAccountId is int id && Accounts.TryGetValue(id, out PortalAccount? a) ? a.Login : null;
            List<AuditEntry> all = AuditIndexes(projectIds)
                .Where(i => AuditTimes[i] >= query.From && AuditTimes[i] < query.To)
                .Where(i => query.ProjectId == null || Audit[i].ProjectId == query.ProjectId)
                .Where(i => query.Person.Length == 0 || (Login(Audit[i]) ?? "").Contains(query.Person, StringComparison.OrdinalIgnoreCase) || (Audit[i].Target ?? "").Contains(query.Person, StringComparison.OrdinalIgnoreCase))
                .Where(i => query.ActionPrefix.Length == 0 || Audit[i].Action.StartsWith(query.ActionPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(i => query.Outcome.Length == 0 || Audit[i].Outcome == query.Outcome)
                .OrderByDescending(i => AuditTimes[i]).ThenByDescending(i => i)
                .Select(i => new AuditEntry(i + 1, AuditTimes[i], Audit[i].ActorAccountId, Login(Audit[i]), Audit[i].ProjectId,
                    AllProjects.FirstOrDefault(p => p.Id == Audit[i].ProjectId)?.Code, Audit[i].Action, Audit[i].Target, Audit[i].Outcome, Audit[i].Detail))
                .ToList();
            return Task.FromResult(new AuditPage(all.Count, all.Skip((page - 1) * pageSize).Take(pageSize).ToList()));
        }

        public Task<AccountCounts> GetAccountCountsAsync(IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default)
        {
            IEnumerable<PortalAccount> scoped = Accounts.Values.Where(a => projectIds == null || (Memberships.GetValueOrDefault(a.Id) ?? []).Any(m => m.ProjectId is int p && projectIds.Contains(p)));
            return Task.FromResult(new AccountCounts(
                scoped.Count(a => a.Kind == AccountKind.Staff && a.Active), scoped.Count(a => a.Kind == AccountKind.Staff && !a.Active),
                scoped.Count(a => a.Kind == AccountKind.Customer && a.Active), scoped.Count(a => a.Kind == AccountKind.Customer && !a.Active)));
        }

        public Task<IReadOnlyList<ProjectStat>> GetProjectStatsAsync(IReadOnlyCollection<int>? projectIds, DateTime deniedSince, CancellationToken cancellationToken = default)
        {
            int Count(int project, string kind) => Accounts.Values.Count(a => a.Active && a.Kind == kind && (Memberships.GetValueOrDefault(a.Id) ?? []).Any(m => m.ProjectId == project));
            IReadOnlyList<ProjectStat> stats = AllProjects.Where(p => projectIds == null || projectIds.Contains(p.Id))
                .Select(p => new ProjectStat(p.Id, p.Code, p.Name, p.Active, Count(p.Id, AccountKind.Customer), Count(p.Id, AccountKind.Staff),
                    AuditIndexes(null).Count(i => Audit[i].ProjectId == p.Id && Audit[i].Outcome == "DENIED" && AuditTimes[i] >= deniedSince))).OrderBy(p => p.Code).ToList();
            return Task.FromResult(stats);
        }

        public Task<int> CountAuditAsync(DateTime since, string outcome, string? actionPrefix, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditIndexes(projectIds).Count(i => AuditTimes[i] >= since && Audit[i].Outcome == outcome
                && (string.IsNullOrEmpty(actionPrefix) || Audit[i].Action.StartsWith(actionPrefix, StringComparison.OrdinalIgnoreCase))));

        public Task<AccountPage> ListAccountsAsync(string search, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default)
        {
            IEnumerable<PortalAccount> query = Accounts.Values.Where(a => search.Length == 0 || a.Login.Contains(search, StringComparison.OrdinalIgnoreCase) || a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (projectIds != null)
            {
                query = query.Where(a => (Memberships.GetValueOrDefault(a.Id) ?? []).Any(m => m.ProjectId is int p && projectIds.Contains(p)));
            }
            List<AccountSummary> all = query.OrderBy(a => a.Login).Select(a => new AccountSummary(a.Id, a.Login, a.DisplayName, a.Kind, a.Active)).ToList();
            return Task.FromResult(new AccountPage(all.Count, all.Skip((page - 1) * pageSize).Take(pageSize).ToList()));
        }

        public Task<int?> CreateAccountAsync(string login, string displayName, string kind, string passwordHash, NewMembership first, int? grantedBy, CancellationToken cancellationToken = default)
        {
            if (Accounts.Values.Any(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult<int?>(null);
            }
            if (ApiUserTaken(first))
            {
                return Task.FromResult<int?>(null);
            }
            int id = ++_nextAccount;
            Accounts[id] = new PortalAccount(id, login, displayName, kind, true, passwordHash);
            Memberships[id] = [new MembershipRow(++_nextMembership, id, first.ProjectId, first.RoleCode, ScopeOf(first.RoleCode), first.ProjectUserName)];
            return Task.FromResult<int?>(id);
        }

        private int OtherCentralAdministrators(int? excludeAccountId, int? excludeMembershipId) =>
            Accounts.Values.Count(a => a.Active && a.Kind == AccountKind.Staff && a.Id != excludeAccountId
                && (Memberships.GetValueOrDefault(a.Id) ?? []).Any(m => m.ProjectId == null && m.RoleCode == RoleCodes.Administrator && m.Id != excludeMembershipId));

        public Task<bool> SetAccountActiveAsync(int id, bool active, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default)
        {
            if (keepOneCentralAdministrator && OtherCentralAdministrators(id, null) == 0)
            {
                return Task.FromResult(false);
            }
            Accounts[id] = Accounts[id] with { Active = active };
            return Task.FromResult(true);
        }

        public Task UpdatePasswordHashAsync(int id, string passwordHash, CancellationToken cancellationToken = default)
        {
            Accounts[id] = Accounts[id] with { PasswordHash = passwordHash };
            return Task.CompletedTask;
        }

        public Task<MembershipRow?> GetMembershipAsync(int membershipId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Memberships.Values.SelectMany(m => m).FirstOrDefault(m => m.Id == membershipId));

        public Task<bool> AddMembershipAsync(int accountId, NewMembership membership, int? grantedBy, CancellationToken cancellationToken = default)
        {
            List<MembershipRow> rows = (Memberships.GetValueOrDefault(accountId) ?? []).ToList();
            if (rows.Any(r => r.ProjectId == membership.ProjectId && r.RoleCode == membership.RoleCode) || ApiUserTaken(membership))
            {
                return Task.FromResult(false);
            }
            rows.Add(new MembershipRow(++_nextMembership, accountId, membership.ProjectId, membership.RoleCode, ScopeOf(membership.RoleCode), membership.ProjectUserName));
            Memberships[accountId] = rows;
            return Task.FromResult(true);
        }

        public Task<bool> RemoveMembershipAsync(int membershipId, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default)
        {
            if (keepOneCentralAdministrator && OtherCentralAdministrators(null, membershipId) == 0)
            {
                return Task.FromResult(false);
            }
            foreach (int account in Memberships.Keys.ToList())
            {
                Memberships[account] = Memberships[account].Where(m => m.Id != membershipId).ToList();
            }
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ProjectDetail>> GetAllProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectDetail>>(AllProjects.OrderBy(p => p.Code).ToList());

        public Task<int?> CreateProjectAsync(string code, string name, string apiBaseUrl, CancellationToken cancellationToken = default)
        {
            if (AllProjects.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult<int?>(null);
            }
            int id = ++_nextProject;
            AllProjects.Add(new ProjectDetail(id, code, name, apiBaseUrl, true));
            return Task.FromResult<int?>(id);
        }

        public Task<bool> UpdateProjectAsync(int id, string name, string apiBaseUrl, bool active, CancellationToken cancellationToken = default)
        {
            int index = AllProjects.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                return Task.FromResult(false);
            }
            AllProjects[index] = AllProjects[index] with { Name = name, ApiBaseUrl = apiBaseUrl, Active = active };
            return Task.FromResult(true);
        }
    }
}
