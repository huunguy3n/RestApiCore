using Business;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class LogsModel(LogService logs) : ProjectPageModel
    {
        public override string NavKey => "logs";

        public int Days { get; private set; } = 1;
        public DateTime? From { get; private set; }
        public DateTime? To { get; private set; }
        public string? UserFilter { get; private set; }
        public string? FormFilter { get; private set; }
        public int? Status { get; private set; }
        public string? ReqId { get; private set; }
        public int PageNumber { get; private set; } = 1;
        public ProjectPerformance? Performance { get; private set; }
        public LogPage? Page_ { get; private set; }

        public string NextUrl => $"/{MaDa}/logs?days={Days}&from={From:yyyy-MM-ddTHH:mm}&to={To:yyyy-MM-ddTHH:mm}&user={Uri.EscapeDataString(UserFilter ?? "")}"
            + $"&form={Uri.EscapeDataString(FormFilter ?? "")}&status={Status}&reqId={Uri.EscapeDataString(ReqId ?? "")}&page={PageNumber + 1}";

        public async Task<IActionResult> OnGetAsync(int? days, DateTime? from, DateTime? to, string? user, string? form, int? status, string? reqId, int? page)
        {
            if (!CanAdmin)
            {
                return Denied();
            }
            Days = Math.Clamp(days ?? 1, 1, 31);
            (From, To, UserFilter, FormFilter, Status, ReqId, PageNumber) = (from, to, user, form, status, reqId, Math.Max(page ?? 1, 1));
            OpResult perf = await logs.PerformanceAsync(Identity!, Project, Days, Aborted);
            Performance = perf.Success ? (ProjectPerformance)perf.Data! : null;
            OpResult list = await logs.ListAsync(Identity!, Project, from, to, user, form, status, reqId, PageNumber, Aborted);
            if (list.Success)
            {
                Page_ = (LogPage)list.Data!;
            }
            else
            {
                Report(list, "");
            }
            return Page();
        }
    }
}
