using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class IndexModel(IPortalStore store, LogService logs, TimeProvider time) : CpanelPageModel
    {
        public override string NavKey => "overview";

        public int Days { get; private set; } = 1;
        public int TotalProjects { get; private set; }
        public int ActiveProjects { get; private set; }
        public int Admins { get; private set; }
        public IReadOnlyList<ProjectPerformance> Performance { get; private set; } = [];
        public IReadOnlyList<AuditEntryView> Recent { get; private set; } = [];

        public async Task OnGetAsync(int? days)
        {
            Days = Math.Clamp(days ?? 1, 1, 31);
            IReadOnlyList<PortalProject> projects = await store.ListProjectsAsync(Aborted);
            TotalProjects = projects.Count;
            ActiveProjects = projects.Count(p => p.Active);
            Admins = (await store.ListAdminsAsync(Aborted)).Count(a => a.Active);
            OpResult dashboard = await logs.DashboardAsync(Identity!, Days, Aborted);
            Performance = dashboard.Success ? (IReadOnlyList<ProjectPerformance>)dashboard.Data! : [];
            DateTime now = time.GetLocalNow().DateTime;
            Recent = (await store.ListAuditAsync(new AuditQuery(now.AddDays(-7), now.AddMinutes(1), null, null, null, null), 1, 10, Aborted)).Items;
        }
    }
}
