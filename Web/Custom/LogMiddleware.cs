using Business;
using System.Diagnostics;

namespace Web.Custom
{
    public class LogMiddleware
    {
        private readonly RequestDelegate _next;

        public LogMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext httpContext, LogManager logManager)
        {
            var stopwatch = Stopwatch.StartNew();

            Guid requestID = Guid.NewGuid();
            httpContext.Items["requestID"] = requestID;

            var request = RequestHelper.GetRequest(httpContext.Request);
            
            // Capture response
            var originalBody = httpContext.Response.Body;
            using var memStream = new MemoryStream();
            httpContext.Response.Body = memStream;

            httpContext.Response.OnStarting(() =>
            {
                httpContext.Response.Headers["X-Request-ID"] = requestID.ToString();
                return Task.CompletedTask;
            });

            // Thực thi middleware tiếp theo
            await _next(httpContext); //gọi đúng 1 lần, có await

            stopwatch.Stop();
            var elapsed = stopwatch.ElapsedMilliseconds;

            // Đọc response
            memStream.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(memStream).ReadToEndAsync();
            memStream.Seek(0, SeekOrigin.Begin);

            logManager.WriteLogData(
                requestID.ToString(),
                elapsed,
                request["info"],
                request["params"],
                request["headers"],
                request["body"]["raw"],
                httpContext.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                httpContext.Response.StatusCode,
                responseBody
            );

            await memStream.CopyToAsync(originalBody); // ghi lại response ra ngoài
            httpContext.Response.Body = originalBody;
        }
    }

    // Extension method used to add the middleware to the HTTP request pipeline.
    public static class LogMiddlewareExtensions
    {
        public static IApplicationBuilder UseLogMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<LogMiddleware>();
        }
    }
}
