using Business;
using DataAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Models;
using Models.Options;
using Serilog;
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
appConfig.pathDirectory = builder.Environment.ContentRootPath;

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

builder.Services.AddControllers(options =>
{
    options.Filters.Add<LogActionFilter>();
});
builder.Services.AddMemoryCache();

builder.Services.AddHealthChecks()
    .AddCheck<SqlHealthCheck>("sql", tags: ["ready"])
    .AddCheck<LogDirectoryHealthCheck>("logDirectory", tags: ["ready"]);

builder.Services.AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, CustomJwtAuthenticationHandler>("Bearer", null);
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseMiddleware<RequestIdMiddleware>();
app.UseSerilogRequestLogging();

// /health/* không ghi log SQL, không tính rate limit
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch =>
{
    branch.UseMiddleware<LogMiddleware>();
    branch.UseMiddleware<RateLimitMiddleware>();
});

app.UseAuthentication();
app.UseAuthorization();

app.UseExceptionHandler(builder =>
{
    builder.Run(async context =>
    {
        LogManager logManager = context.RequestServices.GetRequiredService<LogManager>();
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.RequestServices.GetRequiredService<ILogger<Program>>().LogError(exception, "Lỗi chưa xử lý tại {Path}", context.Request.Path);
        
        logManager.WriteLogError(context.Request.Path, exception?.Message ?? "An error occurred during execution", exception?.StackTrace ?? "");

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
app.MapControllers();

app.Run();
