using Business;
using Microsoft.AspNetCore.Authentication;
using Models;
using System.Text.Encodings.Web;

namespace Web.Custom
{
    public class CustomJwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly AppConfig _appConfig;
        private readonly ResponseManager _responseManager;

        public CustomJwtAuthenticationHandler(Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppConfig appConfig, ResponseManager responseManager) : base(options, logger, encoder)
        {
            _appConfig = appConfig;
            _responseManager = responseManager;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Extract token from the Authorization header
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return AuthenticateResult.NoResult();
            }

            string token = Request.Headers["Authorization"].ToString().Split(" ").Last();
            string ipAddress = Context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";

            AuthManager authManager = new AuthManager(_appConfig);

            try
            {
                // Validate the token
                var principal = authManager.AuthenticateToken(token, ipAddress);
                // Không tự ghi response ở đây: request vẫn đi tiếp, ghi ở đây sẽ bị ghi đè hoặc ghi hai lần.
                // Route [Authorize] trả 401 qua HandleChallengeAsync, /api/v3 trả 401 ở SignatureMiddleware
                if (principal == null)
                {
                    return AuthenticateResult.Fail("Invalid token");
                }

                var ticket = new AuthenticationTicket(principal, Scheme.Name);
                return AuthenticateResult.Success(ticket);
            }
            catch (Exception ex)
            {
                return AuthenticateResult.Fail(ex);
            }
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            await ResponseHelper.WriteJsonAsync(Context, _responseManager.GetResponse("UNAUTHORIZED"));
        }

        protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            await ResponseHelper.WriteJsonAsync(Context, _responseManager.GetResponse("FORBIDDEN"));
        }
    }
}
