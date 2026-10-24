using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class LogDetailModel(LogService logs) : ProjectPageModel
    {
        public override string NavKey => "logs";

        [BindProperty(SupportsGet = true)] public string? ReqId { get; set; }

        public LogDetailView? Detail { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!CanAdmin)
            {
                return Denied();
            }
            OpResult result = await logs.DetailAsync(Identity!, Project, ReqId, ClientIp, Aborted);
            if (result.Code == "NOT_FOUND")
            {
                return NotFound();
            }
            if (result.Success)
            {
                Detail = (LogDetailView)result.Data!;
            }
            else
            {
                Report(result, "");
            }
            return Page();
        }
    }
}
