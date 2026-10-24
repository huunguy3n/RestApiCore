using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ApiDocs.ControlPlane
{
    /// <summary>
    /// Kiểm lại cookie ở MỌI request: tài khoản còn tồn tại và còn hoạt động, và "dấu vết mật khẩu" trong cookie còn khớp.
    /// Nhờ đó khóa tài khoản hoặc đổi mật khẩu làm mọi phiên cũ (kể cả phiên bị đánh cắp) mất hiệu lực ngay, không phải đợi cookie hết hạn.
    /// Kho chưa cấu hình thì không có phiên nào hợp lệ
    /// </summary>
    public sealed class PortalCookieEvents : CookieAuthenticationEvents
    {
        public const string StampClaim = "stamp";

        public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            bool valid = false;
            string? stamp = context.Principal?.FindFirst(StampClaim)?.Value;
            if (int.TryParse(context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int accountId) && !string.IsNullOrEmpty(stamp))
            {
                try
                {
                    IControlPlaneStore store = context.HttpContext.RequestServices.GetRequiredService<IControlPlaneStore>();
                    PortalAccount? account = await store.FindAccountAsync(accountId, context.HttpContext.RequestAborted);
                    valid = account is { Active: true }
                        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stamp), Encoding.ASCII.GetBytes(AccountLoginService.StampOf(account)));
                }
                catch (InvalidOperationException)
                {
                    valid = false;
                }
            }

            if (!valid)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        }
    }
}
