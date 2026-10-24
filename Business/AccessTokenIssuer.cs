using Models;
using Security;

namespace Business
{
    public sealed record IssuedToken(string AccessToken, int ExpiresIn);

    /// <summary>
    /// Cấp JWT v3 (KE_HOACH 4.2.5): sub, u_id, user_id, name_erp, scope=api, iat, jti ngẫu nhiên; hạn theo tokenMinutes.
    /// Chỉ cấp cho tài khoản đã xác thực và đã kiểm IP
    /// </summary>
    public sealed class AccessTokenIssuer(JwtTokenService jwt, AppConfig appConfig, TimeProvider time)
    {
        public IssuedToken IssueApiToken(SignerKeys signer)
        {
            DateTime expires = time.GetUtcNow().UtcDateTime.AddMinutes(appConfig.tokenMinutes);
            string accessToken = jwt.GenerateToken(signer.Name, signer.UId, signer.UserId!.Value, signer.NameErp, Guid.NewGuid().ToString(), expires);
            return new IssuedToken(accessToken, appConfig.tokenMinutes * 60);
        }
    }
}
