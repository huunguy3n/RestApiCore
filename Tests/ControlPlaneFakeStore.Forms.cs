using ApiDocs.ControlPlane;

namespace Tests
{
    internal sealed partial class ControlPlaneFakeStore
    {
        public sealed record StoredRevision(int Id, int ProjectId, string Form, int Revision, int? BaseRevision, string Status, string Content, string Sha, int CreatedBy, DateTime CreatedAt, int? PublishedBy, DateTime? PublishedAt);
        public sealed record StoredPackage(int Id, int ProjectId, int CreatedBy, DateTime CreatedAt, string Manifest, string Sha, List<int> RevisionIds);

        private int _nextRevisionId = 9000, _nextPackage = 0, _nextSync = 0;
        public List<StoredRevision> Revisions { get; } = [];
        public List<StoredPackage> Packages { get; } = [];
        public List<SyncLogEntry> SyncLog { get; } = [];

        private string? LoginOf(int? id) => id is int v && Accounts.TryGetValue(v, out PortalAccount? a) ? a.Login : null;

        private RevisionInfo Info(StoredRevision r) => new(r.Id, r.Revision, r.BaseRevision, r.Status, r.Sha, r.CreatedAt, LoginOf(r.CreatedBy), r.PublishedAt, LoginOf(r.PublishedBy));

        public Task<IReadOnlyList<FormSummary>> ListFormsAsync(int projectId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<FormSummary> list = Revisions.Where(r => r.ProjectId == projectId).GroupBy(r => r.Form).OrderBy(g => g.Key).Select(g =>
            {
                StoredRevision latest = g.OrderByDescending(r => r.Revision).First();
                int? active = g.Where(r => r.Status == "published").Select(r => (int?)r.Revision).Max();
                return new FormSummary(g.Key, latest.Revision, latest.Status, active, latest.CreatedAt, LoginOf(latest.CreatedBy));
            }).ToList();
            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<RevisionInfo>> GetRevisionsAsync(int projectId, string form, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RevisionInfo>>(Revisions.Where(r => r.ProjectId == projectId && r.Form == form).OrderByDescending(r => r.Revision).Select(Info).ToList());

        public Task<RevisionContent?> GetRevisionAsync(int projectId, string form, int revision, CancellationToken cancellationToken = default)
        {
            StoredRevision? r = Revisions.FirstOrDefault(x => x.ProjectId == projectId && x.Form == form && x.Revision == revision);
            return Task.FromResult(r == null ? null : new RevisionContent(Info(r), r.Content));
        }

        public Task<int?> AddRevisionAsync(int projectId, string form, int? baseRevision, string content, string sha256, string status, int createdBy, CancellationToken cancellationToken = default)
        {
            int? current = Revisions.Where(r => r.ProjectId == projectId && r.Form == form).Select(r => (int?)r.Revision).Max();
            if (current != baseRevision)
            {
                return Task.FromResult<int?>(null);
            }
            int next = (current ?? 0) + 1;
            Revisions.Add(new StoredRevision(++_nextRevisionId, projectId, form, next, baseRevision, status, content, sha256, createdBy, DateTime.Now, null, null));
            return Task.FromResult<int?>(next);
        }

        public Task<bool> PublishRevisionAsync(int projectId, string form, int revision, int publishedBy, CancellationToken cancellationToken = default)
        {
            int index = Revisions.FindIndex(r => r.ProjectId == projectId && r.Form == form && r.Revision == revision && r.Status == "validated");
            if (index < 0)
            {
                return Task.FromResult(false);
            }
            Revisions[index] = Revisions[index] with { Status = "published", PublishedBy = publishedBy, PublishedAt = DateTime.Now };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ActiveForm>> GetActiveFormsAsync(int projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveForm>>(Revisions.Where(r => r.ProjectId == projectId && r.Status == "published").GroupBy(r => r.Form).OrderBy(g => g.Key)
                .Select(g => g.OrderByDescending(r => r.Revision).First()).Select(r => new ActiveForm(r.Form, r.Id, r.Revision, r.Content)).ToList());

        public Task<int> CreatePackageAsync(int projectId, int createdBy, Func<int, PackageDraft> build, CancellationToken cancellationToken = default)
        {
            int id = ++_nextPackage;
            PackageDraft draft = build(id);
            Packages.Add(new StoredPackage(id, projectId, createdBy, DateTime.Now, draft.ManifestJson, draft.ManifestSha256, draft.RevisionIds.ToList()));
            return Task.FromResult(id);
        }

        private PackageInfo PackageInfoOf(StoredPackage p) => new(p.Id, p.ProjectId, p.CreatedAt, LoginOf(p.CreatedBy), p.Sha, p.RevisionIds.Count);

        public Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(int projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageInfo>>(Packages.Where(p => p.ProjectId == projectId).OrderByDescending(p => p.Id).Select(PackageInfoOf).ToList());

        public Task<PackageRecord?> GetPackageAsync(int projectId, int packageId, CancellationToken cancellationToken = default)
        {
            StoredPackage? p = Packages.FirstOrDefault(x => x.ProjectId == projectId && x.Id == packageId);
            if (p == null)
            {
                return Task.FromResult<PackageRecord?>(null);
            }
            List<PackageItem> items = p.RevisionIds.Select(id => Revisions.First(r => r.Id == id)).OrderBy(r => r.Form).Select(r => new PackageItem(r.Form, r.Revision, r.Content)).ToList();
            return Task.FromResult<PackageRecord?>(new PackageRecord(PackageInfoOf(p), p.Manifest, items));
        }

        public Task AddSyncLogAsync(SyncLogRecord record, CancellationToken cancellationToken = default)
        {
            SyncLog.Add(new SyncLogEntry(++_nextSync, DateTime.Now, record.ProjectId, AllProjects.FirstOrDefault(p => p.Id == record.ProjectId)?.Code ?? "", record.Env, record.Event,
                record.PackageId, record.Source, record.Actor, record.Outcome, record.Detail));
            return Task.CompletedTask;
        }

        public Task<SyncLogPage> ListSyncLogAsync(IReadOnlyCollection<int> projectIds, int? projectId, string env, string outcome, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            List<SyncLogEntry> all = SyncLog.Where(e => projectIds.Contains(e.ProjectId) && (projectId == null || e.ProjectId == projectId) && (env.Length == 0 || e.Env == env) && (outcome.Length == 0 || e.Outcome == outcome))
                .OrderByDescending(e => e.Id).ToList();
            return Task.FromResult(new SyncLogPage(all.Count, all.Skip((page - 1) * pageSize).Take(pageSize).ToList()));
        }
    }
}
