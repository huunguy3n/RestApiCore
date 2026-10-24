using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    /// <summary>/{ma_da}/login: Admin của dự án hoặc user Api của dự án (2 ô user/pass). Dự án phải có nbdmda.status = 1</summary>
    [EnableRateLimiting("login")]
    public class LoginModel(PortalLoginService loginService, SessionService sessions, IDataProtectionProvider protection, TimeProvider time,
        ILogger<LoginModel> logger) : ProjectPageModel
    {
        protected override bool AllowAnonymous => true;

        private LoginViewModel? _view;
        public LoginViewModel View => _view ??= new() { Title = ProjectName, Subtitle = $"Đăng nhập dự án {MaDa}: Admin dự án hoặc tài khoản Api" };

        private LoginHandler Handler => new(this, sessions, protection, time, logger);

        private string Home => $"/{MaDa}";

        public async Task<IActionResult> OnGetAsync(string? ended)
        {
            if (Identity != null && !Identity.IsSuperAdmin)
            {
                return Redirect(Home);
            }
            View.Info = await LoginFlow.EndedMessageAsync(sessions, ended, Aborted);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? login, string? password)
        {
            View.Login = login;
            return await Handler.StartAsync(() => loginService.LoginProjectAsync(Project, login, password, ClientIp, Aborted), View, PortalAuth.ProjectScheme, SignInAsync, Home) ?? Page();
        }

        public async Task<IActionResult> OnPostConfirmAsync(string? pending) =>
            await Handler.ConfirmAsync(pending, View, PortalAuth.ProjectScheme, SignInAsync, Home) ?? Page();

        public async Task<IActionResult> OnPostCancelAsync(string? pending)
        {
            await Handler.CancelAsync(pending, View);
            return Page();
        }
    }
}
