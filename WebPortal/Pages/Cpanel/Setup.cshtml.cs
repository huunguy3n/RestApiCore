using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    /// <summary>
    /// Khởi tạo SA đầu tiên (thay cho lệnh CLI create-portal-account). Chỉ khi chưa có SA và chỉ từ loopback (người có quyền trên máy chủ portal),
    /// để không ai trong mạng nội bộ "giành" được quyền SA trước người cài đặt
    /// </summary>
    [EnableRateLimiting("login")]
    public class SetupModel(IPortalStore store, AccountService accounts) : CpanelPageModel
    {
        protected override bool AllowAnonymous => true;

        public string? Message { get; private set; }

        private bool FromServer => HttpContext.Connection.RemoteIpAddress is IPAddress ip && IPAddress.IsLoopback(ip);

        public async Task<IActionResult> OnGetAsync()
        {
            if (!FromServer)
            {
                return NotFound();
            }
            string? schema = await store.CheckSchemaAsync(Aborted);
            if (schema != null)
            {
                Message = schema;
                return Page();
            }
            if (await store.SuperAdminExistsAsync(Aborted))
            {
                return Redirect("/cpanel/login");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? userName, string? displayName, string? password)
        {
            if (!FromServer)
            {
                return NotFound();
            }
            string? schema = await store.CheckSchemaAsync(Aborted);
            if (schema != null)
            {
                Message = schema;
                return Page();
            }
            if (await store.SuperAdminExistsAsync(Aborted))
            {
                return NotFound();
            }
            OpResult result = await accounts.CreateSuperAdminAsync(userName?.Trim(), displayName, password, ClientIp, Aborted);
            if (result.Success)
            {
                Flash = "Đã tạo Super Admin. Đăng nhập để tiếp tục.";
                return Redirect("/cpanel/login");
            }
            Message = result.Code == "SCHEMA_MISSING" && result.Data is string detail
                ? detail
                : Describe(result.Code);
            return Page();
        }
    }
}
