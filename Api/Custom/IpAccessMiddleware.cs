using Business;
using System.Security.Claims;

namespace Web.Custom
{
    /// <summary>
    /// Kiểm IP được phép / bị chặn. Chạy sau UseAuthentication để biết user
    /// </summary>
    public class IpAccessMiddleware(RequestDelegate next, IIpAccessManager ipAccessManager)
    {
        public async Task Invoke(HttpContext httpContext)
        {
            string ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";

            var response = await ipAccessManager.CheckAsync(ipAddress, GetUserName(httpContext.User), httpContext.RequestAborted);
            if (response.code != "SUCCESS")
            {
                await ResponseHelper.WriteJsonAsync(httpContext, response);
                return;
            }

            await next(httpContext);
        }

        /// <summary>
        /// Tên user từ JWT (claim sub được map sang NameIdentifier), chưa xác thực thì "anonymous"
        /// </summary>
        public static string GetUserName(ClaimsPrincipal user)
        {
            if (user.Identity?.IsAuthenticated != true)
            {
                return "anonymous";
            }
            return user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity.Name ?? "anonymous";
        }
    }
}
