using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebPortal.Pages.Admin
{
    public class LogoutModel : PageModel
    {
        /// <summary>Chỉ POST (kèm antiforgery) mới đăng xuất; GET không làm gì để không bị ép đăng xuất qua liên kết</summary>
        public IActionResult OnGet() => RedirectToPage("/Admin/Index");

        public async Task<IActionResult> OnPostAsync()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToPage("/Admin/Login");
        }
    }
}
