using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;

namespace WebPortal.Pages.Admin.Audit
{
    public class IndexModel(AuditViewService audit) : AdminPageModel
    {
        public AuditView? View { get; private set; }
        public int PageNumber { get; private set; } = 1;
        public int? ProjectId { get; private set; }
        public string? Person { get; private set; }
        public string? ActionPrefix { get; private set; }
        public string? Outcome { get; private set; }
        private DateTime? _from, _to;

        public async Task<IActionResult> OnGetAsync(DateTime? from, DateTime? to, int? projectId, string? person, string? action, string? outcome, int pageNumber = 1)
        {
            (_from, _to, ProjectId, Person, ActionPrefix, Outcome, PageNumber) = (from, to, projectId, person, action, outcome, Math.Max(pageNumber, 1));
            try
            {
                OpResult result = await audit.ListAsync(AccountId, new AuditFilterInput(from, to, projectId, person, action, outcome), PageNumber, HttpContext.RequestAborted);
                if (!result.Success)
                {
                    FlashError = result.Code == "INVALID_INPUT" ? "Bộ lọc chưa hợp lệ (khoảng ngày tối đa 92 ngày, kết quả SUCCESS/DENIED/REJECTED)." : Describe(result.Code);
                    return Page();
                }
                View = (AuditView)result.Data!;
                return Page();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }

        /// <summary>Giữ nguyên bộ lọc khi chuyển trang</summary>
        public Dictionary<string, string?> RouteFor(int page) => new()
        {
            ["from"] = _from?.ToString("yyyy-MM-dd"), ["to"] = _to?.ToString("yyyy-MM-dd"), ["projectId"] = ProjectId?.ToString(),
            ["person"] = Person, ["action"] = ActionPrefix, ["outcome"] = Outcome, ["pageNumber"] = page.ToString()
        };
    }
}
