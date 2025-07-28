using Microsoft.AspNetCore.Authentication;
using Web.Custom;

var builder = WebApplication.CreateBuilder(args);

// Bind AppSettings
var appConfig = new AppConfig();
builder.Configuration.Bind("AppSettings", appConfig);

// Register DI
builder.Services.AddSingleton(appConfig);
builder.Services.AddScoped<Options, OptionsRepository>();

builder.Services.AddControllers();

builder.Services.AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, CustomJwtAuthenticationHandler>("Bearer", null);
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<CustomMiddleware>();
app.MapGet("/", () => "REST API");
app.MapControllers();

app.Run();
