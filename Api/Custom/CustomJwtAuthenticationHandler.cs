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
                if (principal == null)
                {
                    await ResponseHelper.WriteJsonAsync(Context, _responseManager.GetResponse("UNAUTHORIZED"));
                    return AuthenticateResult.NoResult();
                }

                var ticket = new AuthenticationTicket(principal, Scheme.Name);
                return AuthenticateResult.Success(ticket);
            }
            catch (Exception ex)
            {
                await ResponseHelper.WriteJsonAsync(Context, _responseManager.GetResponse("EXCEPTION", _appConfig.isDevState ? ex.Message : ""));
                return AuthenticateResult.NoResult();
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
