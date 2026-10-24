using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class LogoutModel(SessionService sessions) : CpanelPageModel
    {
        protected override bool AllowAnonymous => true;

        public IActionResult OnGet() => Redirect("/cpanel/login");

        public async Task<IActionResult> OnPostAsync()
        {
            if (Identity != null)
            {
                await sessions.EndAsync(Identity.SessionId, Aborted);
            }
            await HttpContext.SignOutAsync(PortalAuth.CpanelScheme);
            return Redirect("/cpanel/login");
        }
    }
}
