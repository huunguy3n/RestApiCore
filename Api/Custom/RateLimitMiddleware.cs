using Business;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Interface;
using Models.Options;

namespace Web.Custom
{
    // You may need to install the Microsoft.AspNetCore.Http.Abstractions package into your project
    public class RateLimitMiddleware(RequestDelegate next, AppConfig appConfig, IMemoryCache cache, ResponseManager responseManager)
    {
        public async Task Invoke(HttpContext httpContext)
        {
            string ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            string userName = httpContext.User.Identity?.IsAuthenticated == true ? httpContext.User.Identity.Name ?? "anonymous" : "anonymous";

            RateLimitManager rateLimitManager = new RateLimitManager(appConfig, cache, responseManager);
            IResponse response = rateLimitManager.IsRequestAllowed(ipAddress, userName);

            string rateLimit = $"{{0}}/{appConfig.maxRequestsPerMinute}";
            if (cache.TryGetValue(string.Format(rateLimitManager.memoryCacheRate, ipAddress), out int requestCount))
            {
                rateLimit = string.Format(rateLimit, requestCount);
            }
            else
            {
                rateLimit = string.Format(rateLimit, 1);
            }

            httpContext.Response.OnStarting(() =>
            {
                httpContext.Response.Headers["X-RateLimit"] = rateLimit;
                return Task.CompletedTask;
            });

            if (response.code != "SUCCESS")
            {
                await ResponseHelper.WriteJsonAsync(httpContext, response);
                return;
            }

            await next(httpContext);
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
