using Microsoft.AspNetCore.Authentication;
using System.Reflection;
using System.Text.Encodings.Web;

namespace Web.Custom
{
    public class CustomJwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly AppConfig _appConfig;
        private readonly Models.Options.Options _options;

        public CustomJwtAuthenticationHandler(Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppConfig appConfig, Models.Options.Options optionsApi) : base(options, logger, encoder)
        {
            _appConfig = appConfig;
            _options = optionsApi;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);

            string ipAddress = Context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            Request request = new Request(ipAddress);

            // Extract token from the Authorization header
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return AuthenticateResult.NoResult();
            }

            string token = Request.Headers["Authorization"].ToString().Split(" ").Last();

            AuthManager authManager = new AuthManager(_appConfig);

            try
            {
                // Validate the token
                var principal = authManager.AuthenticateToken(token, ipAddress);
                if (principal == null)
                {
                    var response = responseManager.GetResponseMessage(request, "UNAUTHORIZED");

                    await Context.Response.WriteAsync(response.Content.ReadAsStringAsync().Result);
                    return AuthenticateResult.NoResult();
                }

                var ticket = new AuthenticationTicket(principal, Scheme.Name);
                return AuthenticateResult.Success(ticket);
            }
            catch (Exception ex)
            {
                IResponse response = responseManager.GetResponse("EXCEPTION", _appConfig.isDevState ? ex.Message : "");

                await Context.Response.WriteAsync(responseManager.GetResponseMessage(request, response).Content.ReadAsStringAsync().Result);
                return AuthenticateResult.NoResult();
            }
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);

            string ipAddress = Context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            Request request = new Request(ipAddress);

            var response = responseManager.GetResponseMessage(request, "UNAUTHORIZED");
            await Context.Response.WriteAsync(response.Content.ReadAsStringAsync().Result);
        }

        protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);

            string ipAddress = Context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            Request request = new Request(ipAddress);

            var response = responseManager.GetResponseMessage(request, "FORBIDDEN");
            await Context.Response.WriteAsync(response.Content.ReadAsStringAsync().Result);
        }
    }
}
