using System.Security.Claims;
using ApiDocs.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApiDocs.Pages.Admin
{
    public class IndexModel(ProjectAccessService access, AuditViewService audit) : PageModel
    {
        public AccountAccess? Access { get; private set; }
        public Dashboard? Dashboard { get; private set; }
        public string? Message { get; private set; }

        /// <summary>Quyền tính lại từ DB mỗi lần: khóa tài khoản hoặc thu hồi membership có hiệu lực ngay, cookie cũ không giữ được quyền</summary>
        public async Task<IActionResult> OnGetAsync()
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int accountId))
            {
                return await SignOutAsync();
            }

            try
            {
                Access = await access.GetAccessAsync(accountId, HttpContext.RequestAborted);
                if (Access == null)
                {
                    return await SignOutAsync();
                }

                // Số liệu tổng quan chỉ hiện phần người xem có quyền (service tự giới hạn phạm vi; khách hàng / deployment nhận rỗng)
                OpResult dashboard = await audit.GetDashboardAsync(accountId, HttpContext.RequestAborted);
                Dashboard = dashboard.Success ? (Dashboard)dashboard.Data! : null;
                if (Dashboard is { Accounts: null, Denied7Days: null, Recent: null } && Dashboard.Projects.Count == 0)
                {
                    Dashboard = null;
                }
                return Page();
            }
            catch (InvalidOperationException)
            {
                Message = "Portal chưa được cấu hình (ControlPlane:ConnectionString).";
                return Page();
            }
        }

        private async Task<IActionResult> SignOutAsync()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToPage("/Admin/Login");
        }
    }
}
