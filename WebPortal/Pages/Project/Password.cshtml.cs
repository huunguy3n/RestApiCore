using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class PasswordModel(AccountService accounts) : ProjectPageModel
    {
        public override string NavKey => "password";
        protected override bool IsPasswordPage => true;

        public bool Required { get; private set; }

        public IActionResult OnGet(int? required)
        {
            if (Identity!.IsSuperAdmin)
            {
                return Redirect("/cpanel/password");
            }
            Required = required == 1;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? current, string? next)
        {
            if (Identity!.IsSuperAdmin)
            {
                return Redirect("/cpanel/password");
            }
            if (Report(await accounts.ChangeOwnPasswordAsync(Identity, current, next, ClientIp, Aborted), "Đã đổi mật khẩu. Đăng nhập lại."))
            {
                await HttpContext.SignOutAsync(PortalAuth.ProjectScheme);
                return Redirect($"/{MaDa}/login");
            }
            return Page();
        }
    }
}
