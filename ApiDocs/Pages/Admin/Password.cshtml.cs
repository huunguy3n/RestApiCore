using ApiDocs.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace ApiDocs.Pages.Admin
{
    public class PasswordModel(AccountAdminService accounts) : AdminPageModel
    {
        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync(string? current, string? replacement)
        {
            try
            {
                if (Report(await accounts.ChangeOwnPasswordAsync(AccountId, current, replacement, HttpContext.RequestAborted), "Đã đổi mật khẩu. Hãy đăng nhập lại bằng mật khẩu mới."))
                {
                    // Dấu vết mật khẩu trong cookie đã lỗi thời: đăng xuất luôn, mọi phiên khác cũng đã bị cắt
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return RedirectToPage("/Admin/Login");
                }
                return RedirectToPage();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }
    }
}
