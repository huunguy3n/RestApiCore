using WebPortal.ControlPlane;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Provider;

// Portal đối tác (KE_HOACH 5.3): trang tĩnh, mọi lời gọi Api đi thẳng từ trình duyệt.
// Mật khẩu mã hóa RSA-OAEP và private_key ký HMAC đều xử lý bằng WebCrypto, site này không thấy bí mật nào
var builder = WebApplication.CreateBuilder(args);

// Bí mật (chuỗi kết nối DB portal) không commit: appsettings.Local.json (đã gitignore, cùng quy ước với Api) hoặc biến môi trường
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// /Admin: control plane (tài khoản staff/customer theo dự án) trong DB portal riêng. Phần "/" vẫn là cổng đối tác tĩnh
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AllowAnonymousToPage("/Admin/Login");
});

string controlPlaneConnection = ControlPlaneConfig.ConnectionString(builder.Configuration);
if (controlPlaneConnection.Length > 0)
{
    builder.Services.AddSingleton(new SqlDatabase(controlPlaneConnection, 30));
    builder.Services.AddSingleton<IControlPlaneStore, SqlControlPlaneStore>();
}
else
{
    builder.Services.AddSingleton<IControlPlaneStore, UnconfiguredControlPlaneStore>();
}
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AccountLoginService>();
builder.Services.AddSingleton<ProjectAccessService>();
builder.Services.AddSingleton<AccountAdminService>();
builder.Services.AddSingleton<AuditViewService>();
builder.Services.AddSingleton<FormManagementService>();

// Phiên bằng cookie HttpOnly + SameSite=Strict; quyền KHÔNG nằm trong cookie (tính lại ở server mỗi request).
// Ngoài Development: cookie bắt buộc Secure và dùng tiền tố __Host- (trình duyệt từ chối nếu thiếu Secure / Path=/ / có Domain)
bool development = builder.Environment.IsDevelopment();
CookieSecurePolicy securePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
string cookiePrefix = development ? "" : "__Host-";

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = cookiePrefix + "fast-portal";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = securePolicy;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.LoginPath = "/Admin/Login";
        options.AccessDeniedPath = "/Admin/Login";
        // Kiểm tài khoản còn hoạt động + dấu vết mật khẩu ở mọi request: khóa / đổi mật khẩu cắt phiên cũ ngay
        options.Events = new PortalCookieEvents();
    });
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

// Portal:ApiBaseUrl rỗng: Api cùng origin với portal. Khác origin thì Api phải khai appSettings:portalOrigins (CORS)
string apiBaseUrl = (app.Configuration["Portal:ApiBaseUrl"] ?? "").Trim().TrimEnd('/');
string apiOrigin = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out Uri? apiUri) ? apiUri.GetLeftPart(UriPartial.Authority) : "";

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    // Script chỉ từ chính site: chặn script lạ đọc private_key trong bộ nhớ trang
    // /Admin có form đăng nhập/đăng xuất gửi về chính site; cổng đối tác tĩnh ("/") không có form
    bool admin = context.Request.Path.StartsWithSegments("/Admin");
    headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' {apiOrigin}".TrimEnd() + $"; frame-ancestors 'none'; base-uri 'none'; form-action {(admin ? "'self'" : "'none'")}";
    if (admin)
    {
        headers.CacheControl = "no-store";
    }
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

app.MapRazorPages();

app.Run();
