using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Caching.Memory;
using WebPortal.Portal;

// WebPortal (nội bộ DEV/UAT, ADR-0001): /cpanel cho SA duy nhất, /{ma_da} cho Admin dự án và user Api của dự án.
// PortalDb riêng; danh sách dự án ở QLDA (nbdmda); connection string dự án đọc từ appsettings.Local.json trong folder restApi của dự án
var builder = WebApplication.CreateBuilder(args);

// Bí mật (chuỗi kết nối PortalDb, QLDA, mật khẩu PFX khóa license) không commit: appsettings.Local.json (gitignore) hoặc biến môi trường
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

PortalSettings settings = new();
builder.Configuration.Bind("Portal", settings);
// Tên khóa cũ (ControlPlane:ConnectionString) vẫn nhận để không phải sửa cấu hình máy chủ đang chạy
if (settings.ConnectionString.Length == 0)
{
    settings.ConnectionString = builder.Configuration["ControlPlane:ConnectionString"] ?? "";
}

builder.Services.AddRazorPages(options =>
{
    // /{ma_da}: đoạn đầu URL là mã dự án (trừ các đoạn dành riêng), xem ProjectCode
    options.Conventions.AddFolderRouteModelConvention("/Project", model =>
    {
        foreach (var selector in model.Selectors)
        {
            selector.AttributeRouteModel!.Order = 10;
        }
    });
});
builder.Services.Configure<RouteOptions>(options => options.ConstraintMap["projectcode"] = typeof(ProjectCodeRouteConstraint));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Qlda);
builder.Services.AddSingleton(settings.License);
builder.Services.AddSingleton<IPortalStore>(new SqlPortalStore(settings.ConnectionString));
builder.Services.AddSingleton<IQldaDirectory, SqlQldaDirectory>();
builder.Services.AddSingleton<IProjectConnectionResolver, ProjectConnectionResolver>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<PortalLoginService>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<ProjectSyncService>();
builder.Services.AddSingleton<ApiUserService>();
builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<LicenseKeyProvider>();
builder.Services.AddSingleton<LicenseService>();
builder.Services.AddSingleton<FormManagementService>();
builder.Services.AddSingleton<ApiDocsService>();

// Cookie HttpOnly + SameSite=Strict; quyền KHÔNG nằm trong cookie (tính lại ở server mỗi request, kèm kiểm phiên đơn).
// Ngoài Development: Secure bắt buộc và tiền tố __Host- (trình duyệt từ chối nếu thiếu Secure / Path=/ / có Domain)
bool development = builder.Environment.IsDevelopment();
CookieSecurePolicy securePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
string cookiePrefix = development ? "" : "__Host-";

void Cookie(CookieAuthenticationOptions options, string scheme)
{
    options.Cookie.Name = cookiePrefix + "fast-portal-" + scheme;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = securePolicy;
    options.Cookie.Path = "/";
    // Hạn cookie dài hơn phiên; phiên thật (rảnh 30 phút / tối đa 8 giờ) do SessionService quyết định
    options.ExpireTimeSpan = SessionService.AbsoluteTimeout;
    options.SlidingExpiration = false;
    options.Events = new PortalCookieEvents(scheme);
}

builder.Services.AddAuthentication(PortalAuth.ProjectScheme)
    .AddCookie(PortalAuth.CpanelScheme, options => Cookie(options, PortalAuth.CpanelScheme))
    .AddCookie(PortalAuth.ProjectScheme, options => Cookie(options, PortalAuth.ProjectScheme));
builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = cookiePrefix + "fast-antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = securePolicy;
});
builder.Services.Configure<CookieTempDataProviderOptions>(options =>
{
    options.Cookie.Name = cookiePrefix + "fast-tempdata";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = securePolicy;
});

// Mỗi lần thử đăng nhập tốn một lần băm PBKDF2: giới hạn theo IP để không dùng trang đăng nhập làm đòn bẩy ăn CPU
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// "Gọi thử" của user Api đi thẳng từ trình duyệt tới WebApi của dự án: connect-src mở cho mọi origin https/http đã khai của các dự án
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    var cache = context.RequestServices.GetRequiredService<IMemoryCache>();
    if (!cache.TryGetValue<string>("csp:connect", out string? connect) || connect == null)
    {
        connect = "'self'";
        try
        {
            var origins = (await context.RequestServices.GetRequiredService<IPortalStore>().ListProjectsAsync(context.RequestAborted))
                .Select(p => Uri.TryCreate(p.ApiBaseUrl, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) : null)
                .Where(o => o != null).Distinct();
            connect = string.Join(" ", new[] { "'self'" }.Concat(origins!));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException)
        {
        }
        cache.Set("csp:connect", connect, TimeSpan.FromMinutes(1));
    }
    headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src {connect}; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    headers.CacheControl = "no-store";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    await next();
});

app.UseHttpsRedirection();

// Code mẫu (.cs, .py, .java, .sh, .mjs) phục vụ dạng text để xem và tải
var contentTypes = new FileExtensionContentTypeProvider();
foreach (string extension in new[] { ".cs", ".py", ".java", ".sh" })
{
    contentTypes.Mappings[extension] = "text/plain; charset=utf-8";
}
contentTypes.Mappings[".mjs"] = "text/javascript; charset=utf-8";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Phiên cũ hỏi định kỳ: có người đăng nhập đúng mật khẩu ở nơi khác đang chờ thay phiên không
app.MapGet("/cpanel/session/notice", (HttpContext context, SessionService sessions) => SessionNoticeEndpoint.HandleAsync(context, sessions, null));
app.MapGet("/{maDa:projectcode}/session/notice", (HttpContext context, SessionService sessions, string maDa) => SessionNoticeEndpoint.HandleAsync(context, sessions, maDa));

app.MapRazorPages();

app.Run();
