using DataAccess;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class UserEditModel(IPortalStore store, ApiUserService users) : ProjectPageModel
    {
        public override string NavKey => "users";

        [BindProperty(SupportsGet = true)] public int Id { get; set; }

        public PortalApiUser? ApiUser { get; private set; }
        public ProjectApiUser? Remote { get; private set; }
        public IReadOnlyList<ErpUser> ErpUsers { get; private set; } = [];
        public IssuedKey? IssuedKey { get; private set; }

        private string Self => $"/{MaDa}/users/{Id}";

        public async Task<IActionResult> OnGetAsync() => !CanAdmin ? Denied() : await LoadAsync() ? Page() : NotFound();

        public async Task<IActionResult> OnPostSaveAsync(string? nameErp, string? displayName, bool active, string? forms, string? ips, bool confirmAll)
        {
            if (string.IsNullOrWhiteSpace(forms) && !confirmAll)
            {
                FlashError = "Danh sách form để trống nghĩa là cấp TẤT CẢ form. Đánh dấu ô xác nhận nếu đúng ý bạn.";
                return Redirect(Self);
            }
            Report(await users.UpdateAsync(Identity!, Project, Id, new ApiUserInput(null, nameErp, displayName, null, active, forms, ips), ClientIp, Aborted), "Đã lưu.");
            if (Flash != null && await store.FindApiUserAsync(Id, Aborted) is { InSync: false })
            {
                FlashError = Describe("SYNC_FAILED");
            }
            return Redirect(Self);
        }

        public async Task<IActionResult> OnPostKeyAsync()
        {
            OpResult result = await users.IssueKeyAsync(Identity!, Project, Id, ClientIp, Aborted);
            if (!Report(result, "Đã cấp khóa mới."))
            {
                return Redirect(Self);
            }
            // Khóa chỉ hiện trong response này (không qua TempData/cookie)
            IssuedKey = (IssuedKey)result.Data!;
            await LoadAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostRevokeAsync()
        {
            Report(await users.RevokeTokensAsync(Identity!, Project, Id, ClientIp, Aborted), "Đã thu hồi mọi token đã phát (hiệu lực trong tối đa vài chục giây).");
            return Redirect(Self);
        }

        public async Task<IActionResult> OnPostSyncAsync()
        {
            Report(await users.ResyncAsync(Identity!, Project, Id, ClientIp, Aborted), "Đã đồng bộ xuống dự án.");
            return Redirect(Self);
        }

        public async Task<IActionResult> OnPostPasswordAsync(string? password)
        {
            Report(await users.ResetPasswordAsync(Identity!, Project, Id, password, ClientIp, Aborted), "Đã đặt lại mật khẩu, phiên của user bị kết thúc.");
            return Redirect(Self);
        }

        private async Task<bool> LoadAsync()
        {
            ApiUser = await store.FindApiUserAsync(Id, Aborted);
            if (ApiUser == null || ApiUser.ProjectId != Project.Id)
            {
                return false;
            }
            Remote = await users.RemoteStatusAsync(Project, ApiUser.UserName, Aborted);
            OpResult erp = await users.ErpUsersAsync(Identity!, Project, Aborted);
            ErpUsers = erp.Success ? (IReadOnlyList<ErpUser>)erp.Data! : [];
            return true;
        }
    }
}
