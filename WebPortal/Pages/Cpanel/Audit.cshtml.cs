using Business;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class AuditModel(IPortalStore store, TimeProvider time) : CpanelPageModel
    {
        public const int PageSize = 50;

        public override string NavKey => "audit";

        public DateTime From { get; private set; }
        public DateTime To { get; private set; }
        public int? Project { get; private set; }
        public string? Actor { get; private set; }
        public string? ActionPrefix { get; private set; }
        public string? Outcome { get; private set; }
        public int PageNumber { get; private set; } = 1;
        public IReadOnlyList<PortalProject> Projects { get; private set; } = [];
        public AuditPage Result { get; private set; } = new(0, []);

        public string NextUrl => $"/cpanel/audit?from={From:yyyy-MM-dd}&to={To.AddDays(-1):yyyy-MM-dd}&project={Project}&actor={Uri.EscapeDataString(Actor ?? "")}"
            + $"&action={Uri.EscapeDataString(ActionPrefix ?? "")}&outcome={Outcome}&page={PageNumber + 1}";

        public async Task OnGetAsync(DateTime? from, DateTime? to, int? project, string? actor, string? action, string? outcome, int? page)
        {
            DateTime today = time.GetLocalNow().Date;
            To = (to ?? today).Date.AddDays(1);
            From = (from ?? today.AddDays(-7)).Date;
            if (From >= To || To - From > TimeSpan.FromDays(92))
            {
                From = To.AddDays(-7);
            }
            Project = project;
            Actor = LogRedaction.Clean(actor, 64);
            ActionPrefix = LogRedaction.Clean(action, 64);
            Outcome = outcome is "SUCCESS" or "DENIED" or "REJECTED" or "FAILED" ? outcome : null;
            PageNumber = Math.Max(page ?? 1, 1);
            Projects = await store.ListProjectsAsync(Aborted);
            Result = await store.ListAuditAsync(new AuditQuery(From, To, Project, Actor, ActionPrefix, Outcome), PageNumber, PageSize, Aborted);
        }
    }
}
