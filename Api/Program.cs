using Business;
using DataAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Models;
using Models.Options;
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

builder.Services.AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, CustomJwtAuthenticationHandler>("Bearer", null);
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseMiddleware<LogMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.UseExceptionHandler(builder =>
{
    builder.Run(async context =>
    {
        LogManager logManager = context.RequestServices.GetRequiredService<LogManager>();
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        
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
app.MapControllers();

app.Run();
