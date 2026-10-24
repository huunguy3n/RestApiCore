using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class IndexModel(IPortalStore store, LogService logs, ApiDocsService docs) : ProjectPageModel
    {
        public override string NavKey => "overview";

        public IReadOnlyList<PortalApiUser> Users { get; private set; } = [];
        public ProjectPerformance? Performance { get; private set; }
        public ApiFormList? Forms { get; private set; }

        public async Task OnGetAsync()
        {
            if (CanAdmin)
            {
                Users = await store.ListApiUsersAsync(Project.Id, Aborted);
                OpResult perf = await logs.PerformanceAsync(Identity!, Project, 1, Aborted);
                Performance = perf.Success ? (ProjectPerformance)perf.Data! : null;
                return;
            }
            OpResult forms = await docs.FormsAsync(Identity!, Project, Aborted);
            Forms = forms.Success ? (ApiFormList)forms.Data! : null;
        }
    }
}
