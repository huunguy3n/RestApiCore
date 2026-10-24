using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WebPortal.Portal;

namespace WebPortal.Pages
{
    /// <summary>Đăng nhập đúng mật khẩu đang chờ xác nhận thay phiên: mang qua form ẩn, đã mã hóa + ký bằng DataProtection, hạn 2 phút</summary>
    public sealed record PendingLogin(Guid ChallengeId, VerifiedAccount Account, DateTimeOffset Expires);

    public static class LoginFlow
    {
        private const string Purpose = "WebPortal.PendingLogin.v1";

        public static string Protect(IDataProtectionProvider provider, PendingLogin pending) =>
            provider.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(pending));

        public static PendingLogin? Unprotect(IDataProtectionProvider provider, string? token, TimeProvider time)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 4096)
            {
                return null;
            }
            try
            {
                PendingLogin? pending = JsonSerializer.Deserialize<PendingLogin>(provider.CreateProtector(Purpose).Unprotect(token));
                return pending != null && pending.Expires > time.GetUtcNow() ? pending : null;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
            {
                return null;
            }
        }

        /// <summary>Thông báo cho người vừa bị thay phiên / hết phiên (đọc từ ?ended=)</summary>
        public static async Task<string?> EndedMessageAsync(SessionService sessions, string? ended, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(ended, out Guid sid))
            {
                return null;
            }
            string? reason = await sessions.RevokedReasonAsync(sid, cancellationToken);
            if (reason == null)
            {
                return null;
            }
            if (reason.StartsWith("replaced", StringComparison.Ordinal))
            {
                string[] parts = reason.Split('|');
                return parts.Length == 3
                    ? $"Phiên của bạn đã bị thay: tài khoản vừa đăng nhập từ IP {parts[1]} lúc {parts[2]}. Nếu không phải bạn, đổi mật khẩu ngay."
                    : "Phiên của bạn đã bị thay bởi một đăng nhập khác.";
            }
            return reason switch
            {
                "idle" => "Phiên đã hết hạn do không thao tác 30 phút.",
                "expired" => "Phiên đã quá 8 giờ, đăng nhập lại.",
                "password" => "Mật khẩu đã đổi, đăng nhập lại.",
                "locked" => "Tài khoản đã bị khóa.",
                "forced" => "Quản trị đã kết thúc phiên của bạn.",
                _ => null
            };
        }
    }
}
