using System.Security.Claims;
using ApiDocs.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApiDocs.Pages.Admin
{
    [EnableRateLimiting("login")]
    public class LoginModel(AccountLoginService login, ILogger<LoginModel> logger) : PageModel
    {
        [BindProperty] public string? Login { get; set; }
        [BindProperty] public string? Password { get; set; }

        [TempData] public string? Flash { get; set; }
        public string? ReturnUrl { get; private set; }
        public string? Message { get; private set; }

        public IActionResult OnGet(string? returnUrl)
        {
            ReturnUrl = SafeReturnUrl(returnUrl);
            return User.Identity?.IsAuthenticated == true ? LocalRedirect(ReturnUrl) : Page();
        }

        /// <summary>CSRF: Razor Pages tự kiểm antiforgery token cho POST. Mọi lỗi đăng nhập dùng chung một thông báo</summary>
        public async Task<IActionResult> OnPostAsync(string? returnUrl)
        {
            ReturnUrl = SafeReturnUrl(returnUrl);
            LoginResult result;
            try
            {
                result = await login.LoginAsync(Login, Password, HttpContext.Connection.RemoteIpAddress?.ToString(), HttpContext.RequestAborted);
            }
            catch (InvalidOperationException)
            {
                Message = "Portal chưa được cấu hình (ControlPlane:ConnectionString). Liên hệ quản trị.";
                return Page();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Đăng nhập control plane lỗi");
                Message = "Không đăng nhập được lúc này. Thử lại sau.";
                return Page();
            }

            if (!result.Success)
            {
                Password = null;
                Message = result.Code == "ACCOUNT_LOCKED" ? "Tài khoản tạm khóa do nhập sai nhiều lần. Thử lại sau ít phút." : "Tài khoản hoặc mật khẩu không đúng.";
                return Page();
            }

            ClaimsPrincipal principal = new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, result.Account!.Id.ToString()),
                new Claim(ClaimTypes.Name, result.Account.DisplayName),
                new Claim("kind", result.Account.Kind),
                new Claim(PortalCookieEvents.StampClaim, AccountLoginService.StampOf(result.Account))
            ], CookieAuthenticationDefaults.AuthenticationScheme));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = false });
            return LocalRedirect(ReturnUrl);
        }

        /// <summary>Chống open redirect: chỉ nhận đường dẫn nội bộ dưới /Admin</summary>
        private string SafeReturnUrl(string? returnUrl) =>
            !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase) ? returnUrl : "/Admin";
    }
}
