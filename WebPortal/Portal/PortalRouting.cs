namespace WebPortal.Portal
{
    /// <summary>Ràng buộc route {maDa:projectcode}: đúng dạng mã dự án và không phải đoạn URL dành riêng của portal</summary>
    public sealed class ProjectCodeRouteConstraint : IRouteConstraint
    {
        public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
            values.TryGetValue(routeKey, out object? value) && ProjectCode.IsValid(value?.ToString());
    }

    public static class SessionNoticeEndpoint
    {
        /// <summary>JSON { pending, ip, at }: phiên hiện tại có đang bị một đăng nhập khác chờ thay không</summary>
        public static async Task<IResult> HandleAsync(HttpContext context, SessionService sessions, string? maDa)
        {
            PortalIdentity? identity = maDa == null ? await PortalAuth.CpanelAsync(context) : await PortalAuth.ProjectAsync(context, maDa);
            if (identity == null)
            {
                return Results.Json(new { authenticated = false });
            }
            SessionNotice? notice = await sessions.NoticeAsync(identity.SessionId, context.RequestAborted);
            return Results.Json(new
            {
                authenticated = true,
                pending = notice != null,
                ip = notice?.IpAddress,
                at = notice?.At.ToString("yyyy-MM-dd HH:mm:ss"),
                userAgent = notice?.UserAgent
            });
        }
    }
}
