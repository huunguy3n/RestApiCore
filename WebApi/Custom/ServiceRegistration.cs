using Business;
using Business.Logging;
using DataAccess;
using Models;
using Models.Options;
using Security;

namespace Web.Custom
{
    /// <summary>
    /// Đăng ký dịch vụ của ứng dụng (composition root). Phần host (Serilog, rate limit, xác thực, health check, MVC) ở Program.cs.
    /// Tách ra để test dựng đúng đồ thị DI này và kiểm (ServiceRegistrationTests)
    /// </summary>
    public static class ServiceRegistration
    {
        public static IServiceCollection AddFastApi(this IServiceCollection services, AppConfig appConfig, PrivateKeyProtector privateKeyProtector)
        {
            services.AddSingleton(appConfig);
            services.AddSingleton<Options, OptionsRepository>();
            services.AddSingleton(TimeProvider.System);
            services.AddMemoryCache();
            // HybridCache chỉ dùng bộ nhớ: không đăng ký IDistributedCache (không Redis/Garnet)
            services.AddHybridCache(options =>
            {
                // Trang phân trang (JSON nén) có thể vượt mặc định 1 MB; mục quá cỡ HybridCache bỏ qua không báo lỗi
                options.MaximumPayloadBytes = 16 * 1024 * 1024;
            });

            // Hạ tầng SQL (DataAccess): một SqlDatabase cho appConnectionString, cài đặt các cổng của Business (Business/Persistence.cs)
            services.AddSingleton(new Provider.SqlDatabase(appConfig.appConnectionString, appConfig.commandTimeout));
            services.AddSingleton<UserRepository>();
            services.AddSingleton<IUserRightsRepository>(provider => provider.GetRequiredService<UserRepository>());
            services.AddSingleton<ServerRepository>();
            services.AddSingleton<IIpAccessRepository>(provider => provider.GetRequiredService<ServerRepository>());
            services.AddSingleton<IRevokedTokenRepository, RevokedTokenRepository>();
            services.AddSingleton<IUnitRightsRepository, UnitRightsRepository>();
            services.AddSingleton<ColumnSchemaRepository>();
            services.AddSingleton<LogRepository>();
            services.AddSingleton<IFormExecutor, FormExecutor>();
            services.AddSingleton<IIdempotencyStore, IdempotencyRepository>();

            // Bảo mật: khóa, JWT, chữ ký
            services.AddSingleton(privateKeyProtector);
            services.AddSingleton(new JwtTokenService(appConfig.secretKey, appConfig.issuerToken));
            services.AddPortalCors(appConfig.portalOrigins);

            // Nghiệp vụ (Business)
            services.AddSingleton<ResponseManager>();
            services.AddSingleton<IIpAccessManager, IpAccessManager>();
            services.AddSingleton<SignerKeyStore>();
            services.AddSingleton<ISignerKeyStore>(provider => provider.GetRequiredService<SignerKeyStore>());
            services.AddSingleton<ISignerCacheInvalidator>(provider => provider.GetRequiredService<SignerKeyStore>());
            services.AddSingleton<NonceStore>();
            services.AddSingleton<IRevokedTokenStore, RevokedTokenStore>();
            services.AddSingleton<LoginLockout>();
            services.AddSingleton<IUnitRightsChecker, UnitRightsChecker>();
            services.AddSingleton<ICursorStore, CursorStore>();
            services.AddSingleton<MessageAuthenticator>();
            services.AddSingleton<AccessTokenIssuer>();
            services.AddSingleton<FormRequestService>();
            services.AddSingleton<FormResponseWriter>();
            // Log v3: request chỉ đẩy vào hàng đợi, LogWorker ghi SQL theo lô, SQL lỗi thì spool ra logDirectory/spool
            services.AddSingleton<RequestLogQueue>();
            services.AddSingleton<IRequestLogSink, SqlRequestLogSink>();
            services.AddSingleton(new RequestLogSpool(Path.Combine(appConfig.logDirectory, "spool")));
            services.AddHostedService<LogWorker>();

            // Form v3: nạp App_Data/Form lúc khởi động, gắn kiểu theo cột thật; file đổi thì FormReloader nạp lại
            services.AddSingleton<FormLoader>();
            services.AddSingleton(provider =>
            {
                string irregular = provider.GetRequiredService<Options>().Irregular.Trim();
                return new FormRuntime(provider.GetRequiredService<FormLoader>().Load(), irregular.Length > 0 ? new System.Text.RegularExpressions.Regex(irregular) : null);
            });
            services.AddHostedService(provider =>
            {
                var loader = provider.GetRequiredService<FormLoader>();
                return new FormReloader(provider.GetRequiredService<FormRuntime>(), loader.Load, loader.Directory, provider.GetRequiredService<ILogger<FormReloader>>());
            });

            return services;
        }
    }
}
