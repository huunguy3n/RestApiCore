using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Web.Custom
{
    /// <summary>
    /// CORS cho portal ApiDocs (KE_HOACH 5.3) chạy ở origin khác Api: trình duyệt tự ký request bằng WebCrypto,
    /// nên phải gửi được các header X-Fast-* và đọc được header chữ ký của response.
    /// Chỉ áp cho /api; không khai portalOrigins thì không mở CORS
    /// </summary>
    public static class PortalCors
    {
        public const string PolicyName = "portal";

        public static readonly string[] AllowedHeaders =
        [
            "Authorization", "Content-Type",
            SignatureMiddleware.UserHeader, SignatureMiddleware.TimestampHeader, SignatureMiddleware.NonceHeader,
            SignatureMiddleware.ContentSha256Header, SignatureMiddleware.SignatureHeader, SignatureMiddleware.IdempotencyKeyHeader
        ];

        /// <summary>Header response trình duyệt cần đọc để kiểm chữ ký response</summary>
        public static readonly string[] ExposedHeaders =
        [
            RequestIdMiddleware.HeaderName,
            SignatureMiddleware.TimestampHeader, SignatureMiddleware.ContentSha256Header, SignatureMiddleware.SignatureHeader
        ];

        public static IServiceCollection AddPortalCors(this IServiceCollection services, string[] origins)
        {
            return services.AddCors(options => options.AddPolicy(PolicyName, policy => Configure(policy, origins)));
        }

        /// <summary>
        /// Đặt trước log và xác thực: preflight OPTIONS trả ngay, không bị SignatureMiddleware từ chối vì thiếu token
        /// </summary>
        public static IApplicationBuilder UsePortalCors(this IApplicationBuilder app, string[] origins)
        {
            if (origins.Length == 0)
            {
                return app;
            }
            return app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), branch => branch.UseCors(PolicyName));
        }

        private static void Configure(CorsPolicyBuilder policy, string[] origins)
        {
            // Không AllowCredentials: JWT đi bằng header Authorization, không dùng cookie
            policy.WithOrigins(origins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).ToArray())
                .WithMethods("GET", "POST")
                .WithHeaders(AllowedHeaders)
                .WithExposedHeaders(ExposedHeaders)
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }
    }
}
