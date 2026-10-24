using Business;
using Newtonsoft.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace Web.Custom
{
    /// <summary>
    /// POST /api/auth/token và /api/auth/revoke (KE_HOACH 4.2.5). Chữ ký request đã được SignatureMiddleware kiểm,
    /// response do middleware ký
    /// </summary>
    public static class AuthEndpoints
    {
        public static void MapAuth(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost(SignatureMiddleware.TokenPath, IssueTokenAsync);
            endpoints.MapPost("/api/auth/revoke", RevokeAsync);
        }

        private static async Task IssueTokenAsync(HttpContext context, IIpAccessManager ipAccess, LoginLockout lockout, AccessTokenIssuer tokens)
        {
            var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;

            // IP theo restApiAllowIp của chính user này (IpAccessMiddleware chạy trước khi biết user)
            string ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            var ipResponse = await ipAccess.CheckAsync(ipAddress, signer.Name, context.RequestAborted);
            if (ipResponse.code != "SUCCESS")
            {
                await ResponseHelper.WriteJsonAsync(context, ipResponse);
                return;
            }

            lockout.Reset(signer.UId);
            IssuedToken token = tokens.IssueApiToken(signer);

            await WriteJsonAsync(context, new
            {
                code = "SUCCESS",
                success = true,
                message = "",
                requestId = context.Items["requestID"]?.ToString(),
                accessToken = token.AccessToken,
                tokenType = "Bearer",
                expiresIn = token.ExpiresIn
            });
        }

        /// <summary>
        /// Thu hồi chính JWT đang dùng (jti). Từ request sau, token này bị từ chối ở bước 1
        /// </summary>
        private static async Task RevokeAsync(HttpContext context, IRevokedTokenStore revokedTokens, ResponseManager responseManager)
        {
            var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;
            string jti = context.User.FindFirst(JwtRegisteredClaimNames.Jti)!.Value;
            long exp = long.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, out long value) ? value : 0;

            await revokedTokens.RevokeAsync(jti, signer.UId, DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime, context.RequestAborted);

            await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("SUCCESS"));
        }

        private static async Task WriteJsonAsync(HttpContext context, object value)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(value), Encoding.UTF8);
        }
    }
}
