using Serilog.Context;

namespace Web.Custom
{
    /// <summary>
    /// Sinh X-Request-ID (= req_id) cho mỗi request, gắn vào log file và trả về header để tra log file với SQL
    /// </summary>
    public class RequestIdMiddleware(RequestDelegate next)
    {
        public const string HeaderName = "X-Request-ID";

        public async Task Invoke(HttpContext httpContext)
        {
            Guid requestID = Guid.NewGuid();
            httpContext.Items["requestID"] = requestID;
            httpContext.TraceIdentifier = requestID.ToString();

            httpContext.Response.OnStarting(() =>
            {
                httpContext.Response.Headers[HeaderName] = requestID.ToString();
                return Task.CompletedTask;
            });

            using (LogContext.PushProperty("RequestId", requestID))
            {
                await next(httpContext);
            }
        }
    }
}
