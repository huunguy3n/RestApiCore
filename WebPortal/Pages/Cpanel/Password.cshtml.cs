using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class PasswordModel(AccountService accounts) : CpanelPageModel
    {
        public override string NavKey => "password";
        protected override bool IsPasswordPage => true;

        public bool Required { get; private set; }

        public void OnGet(int? required) => Required = required == 1;

        public async Task<IActionResult> OnPostAsync(string? current, string? next)
        {
            if (Report(await accounts.ChangeOwnPasswordAsync(Identity!, current, next, ClientIp, Aborted), "Đã đổi mật khẩu. Đăng nhập lại."))
            {
                await HttpContext.SignOutAsync(PortalAuth.CpanelScheme);
                return Redirect("/cpanel/login");
            }
            return Page();
        }
    }
}
