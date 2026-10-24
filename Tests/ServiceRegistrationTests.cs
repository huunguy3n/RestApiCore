using System.Reflection;
using System.Text.Encodings.Web;
using Business;
using Business.Logging;
using DataAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Models;
using Security;
using Web.Custom;

namespace Tests
{
    /// <summary>
    /// Đồ thị DI thật của Api (ServiceRegistration.AddFastApi, Program.cs gọi cùng hàm). Không cần SQL: chỉ dựng đối tượng,
    /// không resolve FormRuntime (nạp form đọc cấu trúc bảng trên SQL)
    /// </summary>
    public class ServiceRegistrationTests
    {
        private static ServiceProvider Build()
        {
            var appConfig = new AppConfig
            {
                pathDirectory = ApiDirectory(),
                appConnectionString = "Server=127.0.0.1,1;Database=khongco;Connect Timeout=1",
                secretKey = "khoa-ky-jwt-chi-dung-cho-test-0123456789",
                logDirectory = Path.Combine(Path.GetTempPath(), "restapicore-di-test")
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFastApi(appConfig, new PrivateKeyProtector(PrivateKeyProtector.GenerateKeyEncryptionKey()));
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        private static string ApiDirectory()
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WebApi", "App_Data", "Options", "Message.xml")))
            {
                dir = dir.Parent;
            }
            return Path.Combine(dir!.FullName, "WebApi");
        }

        /// <summary>Tham số do host/route cấp, không phải dịch vụ của ứng dụng</summary>
        private static bool FromHostOrRoute(Type type) =>
            type == typeof(HttpContext) || type == typeof(RequestDelegate) || type == typeof(CancellationToken) || type == typeof(string) || type == typeof(UrlEncoder);

        private static IEnumerable<Type> Dependencies(MethodBase method) =>
            method.GetParameters().Select(p => p.ParameterType).Where(t => !FromHostOrRoute(t));

        private static MethodInfo Handler(Type type, string name) =>
            type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(type.Name, name);

        [Fact]
        public void DoThiDi_ValidateOnBuild_KhongLoi()
        {
            using ServiceProvider provider = Build();
        }

        /// <summary>
        /// Minimal API suy luận tham số: kiểu không phải dịch vụ sẽ bị coi là body → request lỗi lúc chạy, không lúc khởi động
        /// </summary>
        [Fact]
        public void ThamSoEndpoint_DeuLaDichVu()
        {
            using ServiceProvider provider = Build();
            var isService = provider.GetRequiredService<IServiceProviderIsService>();

            MethodInfo[] handlers =
            [
                Handler(typeof(FormEndpoint), "HandleAsync"),
                Handler(typeof(AuthEndpoints), "IssueTokenAsync"),
                Handler(typeof(AuthEndpoints), "RevokeAsync"),
                Handler(typeof(MetaEndpoints), "ListErrors")
            ];
            var missing = handlers.SelectMany(h => Dependencies(h).Where(t => !isService.IsService(t)).Select(t => $"{h.DeclaringType!.Name}.{h.Name}: {t.Name}")).ToList();

            Assert.Empty(missing);
            // Lambda _meta/server, _meta/ping trong Program.cs
            Assert.True(isService.IsService(typeof(TimeProvider)));
        }

        [Fact]
        public void Middleware_HandlerXacThuc_HealthCheck_DuPhuThuoc()
        {
            using ServiceProvider provider = Build();
            var isService = provider.GetRequiredService<IServiceProviderIsService>();

            Type[] activated =
            [
                typeof(SignatureMiddleware), typeof(IpAccessMiddleware), typeof(RequestLogMiddleware), typeof(RequestIdMiddleware),
                typeof(CustomJwtAuthenticationHandler),
                typeof(SqlHealthCheck), typeof(LogDirectoryHealthCheck), typeof(LogWorker)
            ];
            var missing = activated
                .SelectMany(type => Dependencies(type.GetConstructors().Single())
                    // IOptionsMonitor<AuthenticationSchemeOptions>, ILoggerFactory do AddAuthentication/AddLogging của host cấp
                    .Where(t => !isService.IsService(t) && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Options.IOptionsMonitor<>)))
                    .Select(t => $"{type.Name}: {t.Name}"))
                .ToList();

            Assert.Empty(missing);
        }

        [Fact]
        public void DichVuKhongCanSql_TaoDuoc()
        {
            using ServiceProvider provider = Build();

            Type[] services =
            [
                typeof(ISignerKeyStore), typeof(IRevokedTokenStore), typeof(IIpAccessManager), typeof(IUnitRightsChecker), typeof(ICursorStore),
                typeof(IFormExecutor), typeof(IIdempotencyStore), typeof(IRequestLogSink), typeof(MessageAuthenticator), typeof(AccessTokenIssuer),
                typeof(FormRequestService), typeof(FormResponseWriter), typeof(FormLoader), typeof(LogRepository)
            ];
            foreach (Type type in services)
            {
                Assert.NotNull(provider.GetRequiredService(type));
            }
        }

        [Fact]
        public void CacCongCuaMotRepository_CungMotInstance()
        {
            using ServiceProvider provider = Build();

            Assert.Same(provider.GetRequiredService<UserRepository>(), provider.GetRequiredService<IUserRightsRepository>());
            Assert.Same(provider.GetRequiredService<ServerRepository>(), provider.GetRequiredService<IIpAccessRepository>());
        }
    }
}
