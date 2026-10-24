using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class LogoutModel(SessionService sessions) : ProjectPageModel
    {
        protected override bool AllowAnonymous => true;

        public IActionResult OnGet() => Redirect($"/{MaDa}/login");

        public async Task<IActionResult> OnPostAsync()
        {
            if (Identity is { IsSuperAdmin: false })
            {
                await sessions.EndAsync(Identity.SessionId, Aborted);
            }
            await HttpContext.SignOutAsync(PortalAuth.ProjectScheme);
            return Redirect($"/{MaDa}/login");
        }
    }
}
