using WebPortal.Portal;

namespace Tests.Support
{
    /// <summary>Kho PortalDb trong bộ nhớ: đủ phiên, user Api, license, audit cho test không cần SQL</summary>
    internal sealed class MemoryPortalStore : IPortalStore
    {
        public Dictionary<int, PortalApiUser> Users { get; } = [];
        public Dictionary<Guid, SessionRecord> Sessions { get; } = [];
        public Dictionary<Guid, ChallengeRecord> Challenges { get; } = [];
        public List<LicenseRecord> Licenses { get; } = [];
        public List<AuditRecord> Audits { get; } = [];

        public Task<PortalApiUser?> FindApiUserAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Users.GetValueOrDefault(id));

        public Task<IReadOnlyList<PortalApiUser>> ListApiUsersAsync(int projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PortalApiUser>>(Users.Values.Where(u => u.ProjectId == projectId).ToList());

        public Task MarkApiUserSyncAsync(int id, int pushedVersion, string? error, CancellationToken cancellationToken = default)
        {
            if (Users.TryGetValue(id, out PortalApiUser? user))
            {
                Users[id] = user with { SyncedVersion = error == null ? pushedVersion : user.SyncedVersion, SyncError = error, SyncedAt = error == null ? DateTime.Now : user.SyncedAt };
            }
            return Task.CompletedTask;
        }

        public Task<SessionRecord?> FindSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sessions.GetValueOrDefault(id));

        public Task<SessionRecord?> FindActiveSessionAsync(string accountType, int accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sessions.Values.FirstOrDefault(s => s.AccountType == accountType && s.AccountId == accountId && s.RevokedAt == null));

        public Task<bool> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default)
        {
            if (Sessions.Values.Any(s => s.AccountType == session.AccountType && s.AccountId == session.AccountId && s.RevokedAt == null))
            {
                return Task.FromResult(false);
            }
            Sessions[session.Id] = session;
            return Task.FromResult(true);
        }

        public Task<bool> ReplaceSessionAsync(Guid oldSessionId, SessionRecord session, string reason, CancellationToken cancellationToken = default)
        {
            if (!Sessions.TryGetValue(oldSessionId, out SessionRecord? old))
            {
                return Task.FromResult(false);
            }
            Sessions[oldSessionId] = old with { RevokedAt = session.CreatedAt, RevokedReason = reason };
            Sessions[session.Id] = session;
            return Task.FromResult(true);
        }

        public Task TouchSessionAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (Sessions.TryGetValue(id, out SessionRecord? session))
            {
                Sessions[id] = session with { LastSeenAt = DateTime.Now };
            }
            return Task.CompletedTask;
        }

        public Task RevokeSessionAsync(Guid id, string reason, CancellationToken cancellationToken = default)
        {
            if (Sessions.TryGetValue(id, out SessionRecord? session) && session.RevokedAt == null)
            {
                Sessions[id] = session with { RevokedAt = DateTime.Now, RevokedReason = reason };
            }
            return Task.CompletedTask;
        }

        public Task RevokeAccountSessionsAsync(string accountType, int accountId, string reason, CancellationToken cancellationToken = default)
        {
            foreach (SessionRecord session in Sessions.Values.Where(s => s.AccountType == accountType && s.AccountId == accountId && s.RevokedAt == null).ToList())
            {
                Sessions[session.Id] = session with { RevokedAt = DateTime.Now, RevokedReason = reason };
            }
            return Task.CompletedTask;
        }

        public Task CreateChallengeAsync(ChallengeRecord challenge, CancellationToken cancellationToken = default)
        {
            Challenges[challenge.Id] = challenge;
            return Task.CompletedTask;
        }

        public Task<ChallengeRecord?> FindChallengeAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Challenges.GetValueOrDefault(id));

        public Task<ChallengeRecord?> FindOpenChallengeForSessionAsync(Guid oldSessionId, DateTime now, CancellationToken cancellationToken = default) =>
            Task.FromResult(Challenges.Values.FirstOrDefault(c => c.OldSessionId == oldSessionId && c.ClosedAt == null && c.ExpiresAt > now));

        public Task<bool> CloseChallengeAsync(Guid id, string outcome, CancellationToken cancellationToken = default)
        {
            if (!Challenges.TryGetValue(id, out ChallengeRecord? challenge) || challenge.ClosedAt != null)
            {
                return Task.FromResult(false);
            }
            Challenges[id] = challenge with { ClosedAt = DateTime.Now, Outcome = outcome };
            return Task.FromResult(true);
        }

        public Task AddLicenseAsync(LicenseRecord license, string requestSha256, int issuedBy, CancellationToken cancellationToken = default)
        {
            Licenses.Add(license);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LicenseRecord>> ListLicensesAsync(int? projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LicenseRecord>>(Licenses.Where(l => projectId == null || l.ProjectId == projectId).ToList());

        public Task<bool> RevokeLicenseAsync(Guid id, int? projectId, string reason, CancellationToken cancellationToken = default)
        {
            int index = Licenses.FindIndex(l => l.Id == id && (projectId == null || l.ProjectId == projectId) && l.RevokedAt == null);
            if (index < 0)
            {
                return Task.FromResult(false);
            }
            LicenseRecord current = Licenses[index];
            Licenses[index] = current with { RevokedAt = DateTime.Now, RevokedReason = reason };
            return Task.FromResult(true);
        }

        public Task WriteAuditAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Audits.Add(record);
            return Task.CompletedTask;
        }

        public Task<string?> CheckSchemaAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<PortalAccount?> FindAccountByLoginAsync(string login, CancellationToken cancellationToken = default) => Unused<PortalAccount?>();
        public Task<PortalAccount?> FindAccountAsync(int id, CancellationToken cancellationToken = default) => Unused<PortalAccount?>();
        public Task<bool> SuperAdminExistsAsync(CancellationToken cancellationToken = default) => Unused<bool>();
        public Task<IReadOnlyList<PortalAccount>> ListAdminsAsync(CancellationToken cancellationToken = default) => Unused<IReadOnlyList<PortalAccount>>();
        public Task<int?> CreateAccountAsync(string login, string displayName, string kind, string passwordHash, int? createdBy, CancellationToken cancellationToken = default) => Unused<int?>();
        public Task SetAccountActiveAsync(int id, bool active, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetAccountPasswordAsync(int id, string passwordHash, bool mustChange, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TouchAccountLoginAsync(int id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> LoginNameTakenAsync(string login, CancellationToken cancellationToken = default) => Unused<bool>();
        public Task<PortalProject?> FindProjectAsync(int id, CancellationToken cancellationToken = default) => Unused<PortalProject?>();
        public Task<PortalProject?> FindProjectByCodeAsync(string maDa, CancellationToken cancellationToken = default) => Unused<PortalProject?>();
        public Task<IReadOnlyList<PortalProject>> ListProjectsAsync(CancellationToken cancellationToken = default) => Unused<IReadOnlyList<PortalProject>>();
        public Task<int?> CreateProjectAsync(string maDa, string apiFolder, string apiBaseUrl, int createdBy, CancellationToken cancellationToken = default) => Unused<int?>();
        public Task UpdateProjectAsync(int id, string apiFolder, string apiBaseUrl, bool active, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetProjectCheckAsync(int id, string result, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<int>> GetAdminProjectIdsAsync(int accountId, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<int>>();
        public Task<IReadOnlyList<PortalAccount>> GetProjectAdminsAsync(int projectId, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<PortalAccount>>();
        public Task<bool> IsProjectAdminAsync(int accountId, int projectId, CancellationToken cancellationToken = default) => Unused<bool>();
        public Task AddProjectAdminAsync(int projectId, int accountId, int grantedBy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveProjectAdminAsync(int projectId, int accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PortalApiUser?> FindApiUserByNameAsync(int projectId, string userName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Users.Values.FirstOrDefault(u => u.ProjectId == projectId && string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase)));
        public Task<int?> CreateApiUserAsync(int projectId, NewApiUser user, int createdBy, CancellationToken cancellationToken = default) => Unused<int?>();
        public Task UpdateApiUserAsync(int id, string nameErp, string? displayName, bool active, IReadOnlyList<string> forms, IReadOnlyList<ApiUserIp> ips, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetApiUserPasswordAsync(int id, string passwordHash, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AuditPage> ListAuditAsync(AuditQuery query, int page, int pageSize, CancellationToken cancellationToken = default) => Unused<AuditPage>();
        public Task<IReadOnlyList<FormSummary>> ListFormsAsync(int projectId, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<FormSummary>>();
        public Task<IReadOnlyList<RevisionInfo>> GetRevisionsAsync(int projectId, string form, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<RevisionInfo>>();
        public Task<RevisionContent?> GetRevisionAsync(int projectId, string form, int revision, CancellationToken cancellationToken = default) => Unused<RevisionContent?>();
        public Task<int?> AddRevisionAsync(int projectId, string form, int? baseRevision, string content, string sha256, string status, int createdBy, CancellationToken cancellationToken = default) => Unused<int?>();
        public Task<bool> PublishRevisionAsync(int projectId, string form, int revision, int publishedBy, CancellationToken cancellationToken = default) => Unused<bool>();
        public Task<IReadOnlyList<ActiveForm>> GetActiveFormsAsync(int projectId, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<ActiveForm>>();
        public Task<int> CreatePackageAsync(int projectId, int createdBy, Func<int, PackageDraft> build, CancellationToken cancellationToken = default) => Unused<int>();
        public Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(int projectId, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<PackageInfo>>();
        public Task<PackageRecord?> GetPackageAsync(int projectId, int packageId, CancellationToken cancellationToken = default) => Unused<PackageRecord?>();
        public Task AddSyncLogAsync(SyncLogRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SyncLogPage> ListSyncLogAsync(int projectId, int page, int pageSize, CancellationToken cancellationToken = default) => Unused<SyncLogPage>();

        private static Task<T> Unused<T>() => throw new NotImplementedException();
    }
}
