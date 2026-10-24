using DataAccess;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class UsersModel(ApiUserService users) : ProjectPageModel
    {
        public override string NavKey => "users";

        public IReadOnlyList<PortalApiUser> Users { get; private set; } = [];
        public IReadOnlyList<ErpUser> ErpUsers { get; private set; } = [];
        public string? ErpError { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!CanAdmin)
            {
                return Denied();
            }
            await LoadAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? userName, string? nameErp, string? displayName, string? password, string? forms, string? ips, bool confirmAll)
        {
            if (string.IsNullOrWhiteSpace(forms) && !confirmAll)
            {
                FlashError = "Danh sách form để trống nghĩa là cấp TẤT CẢ form. Đánh dấu ô xác nhận nếu đúng ý bạn.";
                await LoadAsync();
                return Page();
            }
            OpResult result = await users.CreateAsync(Identity!, Project, new ApiUserInput(userName, nameErp, displayName, password, true, forms, ips), ClientIp, Aborted);
            if (result.Success)
            {
                Flash = $"Đã tạo {userName}." + (SyncOk(result.Data) ? " Đã đồng bộ xuống dự án." : " Chưa đồng bộ được xuống dự án: bấm Đồng bộ lại khi dự án truy cập được.");
                return Redirect($"/{MaDa}/users");
            }
            Report(result, "");
            await LoadAsync();
            return Page();
        }

        private static bool SyncOk(object? data) => data?.GetType().GetProperty("sync")?.GetValue(data) is OpResult { Success: true };

        private async Task LoadAsync()
        {
            OpResult list = await users.ListAsync(Identity!, Project, Aborted);
            Users = list.Success ? (IReadOnlyList<PortalApiUser>)list.Data! : [];
            OpResult erp = await users.ErpUsersAsync(Identity!, Project, Aborted);
            if (erp.Success)
            {
                ErpUsers = (IReadOnlyList<ErpUser>)erp.Data!;
            }
            else
            {
                ErpError = "Không đọc được danh sách NSD FBO của dự án: " + (erp.Data as string ?? Describe(erp.Code));
            }
        }
    }
}
