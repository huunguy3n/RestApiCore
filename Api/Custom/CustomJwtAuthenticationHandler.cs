using Business;
using Microsoft.AspNetCore.Authentication;
using Security;
using System.Text.Encodings.Web;

namespace Web.Custom
{
    /// <summary>
    /// Đọc Bearer JWT (kiểu cũ audience = IP hoặc v3 audience = fast-api) thành ClaimsPrincipal. Chỉ xác thực:
    /// quyền form/đơn vị, thu hồi jti, khớp u_id kiểm ở SignatureMiddleware và FormRequestService
    /// </summary>
    public class CustomJwtAuthenticationHandler(Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        JwtTokenService jwtTokenService, ResponseManager responseManager) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Extract token from the Authorization header
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            string token = Request.Headers["Authorization"].ToString().Split(" ").Last();
            string ipAddress = Context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";

            // Không tự ghi response ở đây: request vẫn đi tiếp, ghi ở đây sẽ bị ghi đè hoặc ghi hai lần.
            // Route [Authorize] trả 401 qua HandleChallengeAsync, /api trả 401 ở SignatureMiddleware
            var principal = jwtTokenService.ValidateJWTToken(token, ipAddress);
            if (principal == null)
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid token"));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            await ResponseHelper.WriteJsonAsync(Context, responseManager.GetResponse("UNAUTHORIZED"));
        }

        protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            await ResponseHelper.WriteJsonAsync(Context, responseManager.GetResponse("FORBIDDEN"));
        }
    }
}
