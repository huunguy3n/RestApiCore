using Models;
using Security;

namespace Business
{
    public sealed record IssuedToken(string AccessToken, int ExpiresIn);

    /// <summary>
    /// Cấp JWT v3 (KE_HOACH 4.2.5): sub, u_id, user_id, name_erp, scope, jti ngẫu nhiên; hạn theo tokenMinutes (api)
    /// hoặc portalTokenMinutes (portal). Chỉ cấp cho tài khoản đã xác thực và đã kiểm IP
    /// </summary>
    public sealed class AccessTokenIssuer(JwtTokenService jwt, AppConfig appConfig, TimeProvider time)
    {
        public IssuedToken IssueApiToken(SignerKeys signer) => Issue(signer, appConfig.tokenMinutes, "api");

        /// <summary>scope=portal: SignatureMiddleware chỉ cho gọi GET /api/_meta/* (trừ ping)</summary>
        public IssuedToken IssuePortalToken(SignerKeys account) => Issue(account, appConfig.portalTokenMinutes, "portal");

        private IssuedToken Issue(SignerKeys signer, int minutes, string scope)
        {
            DateTime expires = time.GetUtcNow().UtcDateTime.AddMinutes(minutes);
            string accessToken = jwt.GenerateToken(signer.Name, signer.UId, signer.UserId!.Value, signer.NameErp, Guid.NewGuid().ToString(), expires, scope);
            return new IssuedToken(accessToken, minutes * 60);
        }
    }
}
