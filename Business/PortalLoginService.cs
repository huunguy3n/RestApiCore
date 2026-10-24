using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Models;
using Security;

namespace Business
{
    /// <param name="Code">SUCCESS hoặc mã lỗi Message.xml</param>
    /// <param name="AccountFound">Đã xác định được tài khoản còn hiệu lực (để log ghi PORTAL_LOGIN)</param>
    public sealed record PortalLoginResult(string Code, bool AccountFound = false, IssuedToken? Token = null);

    /// <summary>
    /// Đăng nhập portal đối tác (KE_HOACH 5.3): restApiUser.name + mật khẩu mã hóa RSA-OAEP tại trình duyệt.
    /// Thứ tự kiểm như auth/token: tài khoản → khóa đăng nhập → IP → kid → giải RSA → timestamp + nonce → so mật khẩu (thời gian hằng).
    /// Sai mật khẩu hoặc bản mã giải không được thì tính vào khóa đăng nhập
    /// </summary>
    public sealed partial class PortalLoginService(AppConfig appConfig, ISignerKeyStore keyStore, IPortalCredentialStore credentials, PortalKeyProvider portalKey,
        NonceStore nonceStore, LoginLockout lockout, IIpAccessManager ipAccess, AccessTokenIssuer tokens, TimeProvider time)
    {
        [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
        private static partial Regex NonceFormat();

        /// <summary>Nội dung đã giải RSA của trường credential</summary>
        private sealed record PortalCredential(string? password, long timestamp, string? nonce);

        public async Task<PortalLoginResult> LoginAsync(string userName, string kid, string credential, string clientIp, CancellationToken cancellationToken = default)
        {
            SignerKeys? account = await keyStore.GetAsync(userName, cancellationToken);
            if (account == null || !account.IsActive)
            {
                return new PortalLoginResult("UNAUTHORIZED");
            }

            if (lockout.IsLocked(account.UId))
            {
                return new PortalLoginResult("ACCOUNT_LOCKED", true);
            }

            var ipResponse = await ipAccess.CheckAsync(clientIp, account.Name, cancellationToken);
            if (ipResponse.code != "SUCCESS")
            {
                return new PortalLoginResult(ipResponse.code, true);
            }

            if (kid != portalKey.Kid)
            {
                // Trình duyệt dùng khóa công khai cũ (server đã đổi khóa): lấy lại /_meta/server
                return new PortalLoginResult("INVALID_KID", true);
            }

            PortalCredential? decrypted = Parse(portalKey.Decrypt(credential));
            if (decrypted is not { password.Length: > 0, nonce: not null })
            {
                lockout.RecordFailure(account.UId);
                return new PortalLoginResult("INVALID_CREDENTIAL", true);
            }

            if (Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - decrypted.timestamp) > appConfig.clockSkewSeconds)
            {
                return new PortalLoginResult("TIMESTAMP_EXPIRED", true);
            }
            if (!NonceFormat().IsMatch(decrypted.nonce) || !nonceStore.TryUse(account.UId, "portal:" + decrypted.nonce, NonceStore.ReplayWindow(appConfig.clockSkewSeconds)))
            {
                // Phát lại gói đăng nhập đã bắt được
                return new PortalLoginResult("NONCE_REUSED", true);
            }

            if (!await PasswordMatchesAsync(account.Name, decrypted.password, cancellationToken))
            {
                lockout.RecordFailure(account.UId);
                return new PortalLoginResult("INVALID_CREDENTIAL", true);
            }

            lockout.Reset(account.UId);
            return new PortalLoginResult("SUCCESS", true, tokens.IssuePortalToken(account));
        }

        /// <summary>
        /// restApiUser.password giải bằng passwordKey rồi so theo thời gian hằng
        /// </summary>
        private async Task<bool> PasswordMatchesAsync(string userName, string password, CancellationToken cancellationToken)
        {
            string? stored = await credentials.GetEncryptedPasswordAsync(userName, cancellationToken);
            if (string.IsNullOrEmpty(stored))
            {
                return false;
            }
            string plain;
            try
            {
                plain = EncryptionHelper.DecryptString(stored, appConfig.passwordKey);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return false;
            }
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(plain)), SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        }

        private static PortalCredential? Parse(byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<PortalCredential>(data);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
