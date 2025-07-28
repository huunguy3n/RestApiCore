using Business;

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
            var request = httpContext.Request;
            request.EnableBuffering();

            // Đọc body request
            string requestBody;
            using (var reader = new StreamReader(request.Body, leaveOpen: true))
            {
                requestBody = await reader.ReadToEndAsync();
                request.Body.Position = 0;
            }

            // Log request
            // logManager.Log($"[REQUEST] {request.Method} {request.Path} | Body: {requestBody}");

            // Capture response
            var originalBody = httpContext.Response.Body;
            using var memStream = new MemoryStream();
            httpContext.Response.Body = memStream;

            // Thực thi middleware tiếp theo
            await _next(httpContext); // ✅ gọi đúng 1 lần, có await

            // Đọc response
            memStream.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(memStream).ReadToEndAsync();
            memStream.Seek(0, SeekOrigin.Begin);

            // Log response
            // logManager.Log($"[RESPONSE] {context.Response.StatusCode} | Body: {responseBody}");

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
