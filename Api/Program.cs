using Business;
using DataAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Models;
using Models.Options;
using Serilog;
using System.Threading.RateLimiting;
using Web.Custom;

var builder = WebApplication.CreateBuilder(args);

// Bí mật (secretKey, passwordKey, connection string) không commit: đặt ở appsettings.Local.json (đã gitignore) hoặc biến môi trường
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// Bind AppSettings
var appConfig = new AppConfig();
builder.Configuration.Bind("AppSettings", appConfig);

appConfig.appConnectionString = builder.Configuration.GetConnectionString("appConnectionString") ?? "";

if (string.IsNullOrWhiteSpace(appConfig.secretKey) || string.IsNullOrWhiteSpace(appConfig.appConnectionString))
{
    throw new InvalidOperationException("Thiếu appSettings:secretKey hoặc connectionStrings:appConnectionString. Khai báo trong appsettings.Local.json hoặc biến môi trường.");
}
// passwordKey là khóa AES-256 (32 ký tự) mã hóa restApiUser.password, không được trùng secretKey ký JWT
if (appConfig.passwordKey.Length != 32 || appConfig.passwordKey == appConfig.secretKey)
{
    throw new InvalidOperationException("appSettings:passwordKey phải đúng 32 ký tự và khác secretKey. Mã hóa lại mật khẩu bằng: Fast.Api.Cli rekey-passwords.");
}
// Khóa chủ mã hóa restApiUser.private_key (HMAC v3). PrivateKeyProtector báo lỗi nếu không phải base64 32 byte
appConfig.keyEncryptionKey = builder.Configuration["Security:KeyEncryptionKey"] ?? "";
var privateKeyProtector = new Security.PrivateKeyProtector(appConfig.keyEncryptionKey);

appConfig.pathDirectory = builder.Environment.ContentRootPath;
Provider.Provider.Configure(appConfig.commandTimeout);

// Log file chẩn đoán vận hành: ngoài thư mục release, cuộn theo ngày và dung lượng
if (string.IsNullOrWhiteSpace(appConfig.logDirectory))
{
    appConfig.logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FastRestApi", "Logs");
}
Directory.CreateDirectory(appConfig.logDirectory);

builder.Services.AddSerilog(config => config
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(appConfig.logDirectory, "api-.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 31,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {RequestId} {SourceContext} {Message:lj}{NewLine}{Exception}"));

// Register DI
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<Options, OptionsRepository>();
builder.Services.AddSingleton<LogManager>();
builder.Services.AddSingleton<ResponseManager>();
builder.Services.AddSingleton<IIpAccessManager, IpAccessManager>();
builder.Services.AddSingleton(privateKeyProtector);
builder.Services.AddSingleton<ISignerKeyStore, SignerKeyStore>();
builder.Services.AddSingleton<NonceStore>();
builder.Services.AddSingleton<IRevokedTokenStore, RevokedTokenStore>();
builder.Services.AddSingleton<LoginLockout>();
builder.Services.AddSingleton<Business.V3.IUnitRightsChecker, Business.V3.UnitRightsChecker>();
builder.Services.AddSingleton<Business.V3.IFormExecutor, Business.V3.FormExecutor>();
builder.Services.AddSingleton<Business.V3.IIdempotencyStore, Business.V3.IdempotencyStore>();
// Form v3: nạp App_Data/Form một lần, gắn kiểu theo cột thật của bảng FBO (mỗi bảng đọc một lần)
builder.Services.AddSingleton(services =>
{
    var logger = services.GetRequiredService<ILogger<Business.V3.ImportFormCatalog>>();
    var columns = new DataAccess.ColumnSchemaRepository(appConfig);
    var schemaCache = new Dictionary<string, IReadOnlyDictionary<string, Models.V3.ColumnInfo>?>(StringComparer.OrdinalIgnoreCase);

    var catalog = Business.V3.ImportFormCatalog.LoadDirectory(Path.Combine(appConfig.pathDirectory, "App_Data", "Form"), logger,
        form => Business.V3.ColumnTypeResolver.Resolve(form, table =>
        {
            if (!schemaCache.TryGetValue(table, out var schema))
            {
                schema = schemaCache[table] = columns.GetColumnsAsync(table).GetAwaiter().GetResult();
            }
            return schema;
        }));

    string irregular = services.GetRequiredService<Options>().Irregular.Trim();
    return new V3FormRuntime(catalog, irregular.Length > 0 ? new System.Text.RegularExpressions.Regex(irregular) : null);
});
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddControllers(options =>
{
    options.Filters.Add<LogActionFilter>();
});
builder.Services.AddMemoryCache();
// HybridCache chỉ dùng bộ nhớ: không đăng ký IDistributedCache (không Redis/Garnet)
builder.Services.AddHybridCache();

// Rate limit in-process: theo user đã xác thực, chưa xác thực thì theo IP
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        string userName = IpAccessMiddleware.GetUserName(context.User);
        string partitionKey = userName != "anonymous"
            ? $"user:{userName}"
            : $"ip:{context.Connection.RemoteIpAddress}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = appConfig.maxRequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }
        var responseManager = context.HttpContext.RequestServices.GetRequiredService<ResponseManager>();
        await ResponseHelper.WriteJsonAsync(context.HttpContext, responseManager.GetResponse("TOO_MANY_REQUESTS"));
    };
});

builder.Services.AddHealthChecks()
    .AddCheck<SqlHealthCheck>("sql", tags: ["ready"])
    .AddCheck<LogDirectoryHealthCheck>("logDirectory", tags: ["ready"]);

builder.Services.AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, CustomJwtAuthenticationHandler>("Bearer", null);
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseMiddleware<RequestIdMiddleware>();
app.UseSerilogRequestLogging();

// /health/* không ghi log SQL, không tính rate limit, không kiểm IP
static bool IsBusinessRequest(HttpContext context) => !context.Request.Path.StartsWithSegments("/health");

// Log đặt trước xác thực để ghi cả response 401
app.UseWhen(IsBusinessRequest, branch => branch.UseMiddleware<LogMiddleware>());

app.UseAuthentication();

// Rate limit và kiểm IP đặt sau xác thực để biết user
app.UseWhen(IsBusinessRequest, branch =>
{
    branch.UseRateLimiter();
    branch.UseMiddleware<IpAccessMiddleware>();
});

// /api/v3: kiểm chữ ký request, ký response. Đặt ngoài UseExceptionHandler để response lỗi 500 cũng được ký
app.UseMiddleware<SignatureMiddleware>();

app.UseAuthorization();

app.UseExceptionHandler(builder =>
{
    builder.Run(async context =>
    {
        LogManager logManager = context.RequestServices.GetRequiredService<LogManager>();
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.RequestServices.GetRequiredService<ILogger<Program>>().LogError(exception, "Lỗi chưa xử lý tại {Path}", context.Request.Path);
        
        await logManager.WriteLogErrorAsync(context.Request.Path, exception?.Message ?? "An error occurred during execution", exception?.StackTrace ?? "");

        await ResponseHelper.WriteJsonAsync(context,
            new Models.Response.Response
            {
                code = "ERROR",
                success = false,
                message = (appConfig.isDevState ? exception?.ToString() ?? "An error occurred during execution" : "An error occurred during execution"),
                statusCode = 500
            });
    });
});

app.MapGet("/", () => "REST API");
// live: tiến trình còn chạy, không kiểm gì. ready: SQL + thư mục log
app.MapHealthChecks("/health/live", new() { Predicate = _ => false, ResponseWriter = HealthCheckResponse.Writer(appConfig) });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = HealthCheckResponse.Writer(appConfig) });

// v3 meta: server không cần token, không ký (đồng bộ giờ). ping cần JWT + chữ ký, để đối tác thử ký hai chiều
app.MapGet("/api/v3/_meta/server", (TimeProvider time) => Results.Json(new
{
    serverTime = time.GetUtcNow().ToUnixTimeSeconds(),
    signatureVersion = Security.MessageSignature.RequestAlgorithm,
    clockSkewSeconds = appConfig.clockSkewSeconds
}));
app.MapV3Auth();
app.MapPost("/api/v3/_meta/ping", (HttpContext context, TimeProvider time) =>
{
    var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;
    return Results.Json(new
    {
        code = "SUCCESS",
        success = true,
        message = "",
        request_id = context.Items["requestID"]?.ToString(),
        data = new { u_id = signer.UId, name = signer.Name, serverTime = time.GetUtcNow().ToUnixTimeSeconds() }
    });
});
// Route nghiệp vụ v3 từ App_Data/Form (route literal ở trên được ưu tiên)
app.MapV3Forms();
app.MapControllers();

// Nạp form ngay lúc khởi động để lỗi XML hiện trong log, không đợi request đầu
app.Services.GetRequiredService<V3FormRuntime>();

app.Run();
