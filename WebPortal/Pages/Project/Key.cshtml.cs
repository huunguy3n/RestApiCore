using DataAccess;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    /// <summary>User Api: xem lại private_key của mình (nhập lại mật khẩu, ghi nhật ký), thu hồi mọi token của mình</summary>
    public class KeyModel(ApiUserService users) : ProjectPageModel
    {
        public override string NavKey => "key";

        public ProjectApiUser? Remote { get; private set; }
        public IssuedKey? Key { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!IsApi)
            {
                return Denied();
            }
            Remote = await users.RemoteStatusAsync(Project, Identity!.Login, Aborted);
            return Page();
        }

        public async Task<IActionResult> OnPostViewAsync(string? password)
        {
            if (!IsApi)
            {
                return Denied();
            }
            OpResult result = await users.ViewOwnKeyAsync(Identity!, Project, password, ClientIp, Aborted);
            if (result.Success)
            {
                // Khóa chỉ nằm trong response này, không qua TempData/cookie
                Key = (IssuedKey)result.Data!;
            }
            else
            {
                Report(result, "");
            }
            Remote = await users.RemoteStatusAsync(Project, Identity!.Login, Aborted);
            return Page();
        }

        public async Task<IActionResult> OnPostRevokeAsync()
        {
            if (!IsApi)
            {
                return Denied();
            }
            Report(await users.RevokeTokensAsync(Identity!, Project, Identity!.AccountId, ClientIp, Aborted), "Đã thu hồi mọi token (hiệu lực trong tối đa vài chục giây).");
            return Redirect($"/{MaDa}/key");
        }
    }
}
