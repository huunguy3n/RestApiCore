using Business;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Interface;
using Models.Options;

namespace Web.Custom
{
    // You may need to install the Microsoft.AspNetCore.Http.Abstractions package into your project
    public class RateLimitMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly AppConfig _appConfig;
        private readonly Options _options;
        private IMemoryCache _cache;

        public RateLimitMiddleware(RequestDelegate next, AppConfig appConfig, Options options, IMemoryCache cache)
        {
            _next = next;
            _appConfig = appConfig;
            _options = options;
            _cache = cache;
        }

        public async Task Invoke(HttpContext httpContext)
        {
            string ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            string userName = httpContext.User.Identity?.IsAuthenticated == true ? httpContext.User.Identity.Name ?? "anonymous" : "anonymous";

            RateLimitManager rateLimitManager = new RateLimitManager(_appConfig, _options, _cache);
            IResponse response = rateLimitManager.IsRequestAllowed(ipAddress, userName);

            if(response.code != "SUCCESS")
            {
                await ResponseHelper.WriteJsonAsync(httpContext, response);
                return;
            }

            await _next(httpContext);
        }
    }

    // Extension method used to add the middleware to the HTTP request pipeline.
    public static class CustomMiddlewareExtensions
    {
        public static IApplicationBuilder UseCustomMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<RateLimitMiddleware>();
        }
    }
}
