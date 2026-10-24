using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;

namespace WebPortal.Pages.Admin.Accounts
{
    public class IndexModel(AccountAdminService accounts) : AdminPageModel
    {
        public string? Search { get; private set; }
        public int PageNumber { get; private set; } = 1;
        public AccountPage? Page_ { get; private set; }
        public bool CanCreate { get; private set; }

        public async Task<IActionResult> OnGetAsync(string? search, int pageNumber = 1)
        {
            Search = search;
            PageNumber = Math.Max(pageNumber, 1);
            try
            {
                OpResult list = await accounts.ListAccountsAsync(AccountId, search, PageNumber, 25, HttpContext.RequestAborted);
                if (!list.Success)
                {
                    FlashError = Describe(list.Code);
                    return Page();
                }
                Page_ = (AccountPage)list.Data!;
                CanCreate = (await accounts.GetCreateOptionsAsync(AccountId, HttpContext.RequestAborted)).Success;
                return Page();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }
    }
}
