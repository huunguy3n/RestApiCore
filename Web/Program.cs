using Business;
using DataAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Models;
using Models.Options;
using Web.Custom;

var builder = WebApplication.CreateBuilder(args);

// Bind AppSettings
var appConfig = new AppConfig();
builder.Configuration.Bind("AppSettings", appConfig);

appConfig.appConnectionString = builder.Configuration.GetConnectionString("appConnectionString") ?? ".";
appConfig.pathDirectory = builder.Environment.ContentRootPath;

// Register DI
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<Options, OptionsRepository>();
builder.Services.AddScoped<LogManager>();

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
