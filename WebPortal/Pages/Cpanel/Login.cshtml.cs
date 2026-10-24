using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    /// <summary>/cpanel/login: chỉ SA duy nhất. Chưa có SA thì chuyển sang trang khởi tạo (chỉ mở từ chính máy chủ)</summary>
    [EnableRateLimiting("login")]
    public class LoginModel(PortalLoginService loginService, SessionService sessions, IPortalStore store, IDataProtectionProvider protection, TimeProvider time,
        ILogger<LoginModel> logger) : CpanelPageModel
    {
        protected override bool AllowAnonymous => true;

        public LoginViewModel View { get; } = new() { Title = "Fast API · Control Panel", Subtitle = "Đăng nhập Super Admin" };

        private LoginHandler Handler => new(this, sessions, protection, time, logger);

        public async Task<IActionResult> OnGetAsync(string? ended)
        {
            if (Identity != null)
            {
                return Redirect("/cpanel");
            }
            if (!await store.SuperAdminExistsAsync(Aborted))
            {
                return Redirect("/cpanel/setup");
            }
            View.Info = await LoginFlow.EndedMessageAsync(sessions, ended, Aborted);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? login, string? password)
        {
            View.Login = login;
            return await Handler.StartAsync(() => loginService.LoginCpanelAsync(login, password, ClientIp, Aborted), View, PortalAuth.CpanelScheme, SignInAsync, "/cpanel") ?? Page();
        }

        public async Task<IActionResult> OnPostConfirmAsync(string? pending) =>
            await Handler.ConfirmAsync(pending, View, PortalAuth.CpanelScheme, SignInAsync, "/cpanel") ?? Page();

        public async Task<IActionResult> OnPostCancelAsync(string? pending)
        {
            await Handler.CancelAsync(pending, View);
            return Page();
        }
    }
}
