using Business;
using Models;
using Newtonsoft.Json;
using Security;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;

namespace Web.Custom
{
    /// <summary>
    /// POST /api/v3/auth/token và /api/v3/auth/revoke (KE_HOACH_V3 4.2.5). Chữ ký request đã được SignatureMiddleware kiểm,
    /// response do middleware ký
    /// </summary>
    public static class V3AuthEndpoints
    {
        public static void MapV3Auth(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost(SignatureMiddleware.TokenPath, IssueTokenAsync);
            endpoints.MapPost("/api/v3/auth/revoke", RevokeAsync);
        }

        private static async Task IssueTokenAsync(HttpContext context, AppConfig appConfig, ResponseManager responseManager,
            IIpAccessManager ipAccess, LoginLockout lockout, TimeProvider time)
        {
            var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;

            // Body { "userName": "<restApiUser.name>" } phải khớp X-Fast-User (đã ký)
            string? bodyUser = ReadUserName((byte[])context.Items[SignatureMiddleware.RawBodyItem]!);
            if (bodyUser == null)
            {
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("INVALID_SHAPE"));
                return;
            }
            if (!string.Equals(bodyUser.Trim(), signer.Name, StringComparison.OrdinalIgnoreCase))
            {
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("USER_MISMATCH"));
                return;
            }

            // IP theo restApiAllowIp của chính user này (IpAccessMiddleware chạy trước khi biết user)
            string ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            var ipResponse = await ipAccess.CheckAsync(ipAddress, signer.Name, context.RequestAborted);
            if (ipResponse.code != "SUCCESS")
            {
                await ResponseHelper.WriteJsonAsync(context, ipResponse);
                return;
            }

            lockout.Reset(signer.UId);

            DateTime expires = time.GetUtcNow().UtcDateTime.AddMinutes(appConfig.v3TokenMinutes);
            string accessToken = new JwtTokenService(appConfig.secretKey, appConfig.expiryToken, appConfig.issuerToken)
                .GenerateV3Token(signer.Name, signer.UId, signer.UserId!.Value, signer.NameErp, Guid.NewGuid().ToString(), expires);

            await WriteJsonAsync(context, new
            {
                code = "SUCCESS",
                success = true,
                message = "",
                request_id = context.Items["requestID"]?.ToString(),
                accessToken,
                tokenType = "Bearer",
                expiresIn = appConfig.v3TokenMinutes * 60
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

        private static string? ReadUserName(byte[] body)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                return json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("userName", out JsonElement userName)
                    && userName.ValueKind == JsonValueKind.String
                    ? userName.GetString()
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static async Task WriteJsonAsync(HttpContext context, object value)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(value), Encoding.UTF8);
        }
    }
}
