using Microsoft.AspNetCore.StaticFiles;

// Portal đối tác (KE_HOACH 5.3): trang tĩnh, mọi lời gọi Api đi thẳng từ trình duyệt.
// Mật khẩu mã hóa RSA-OAEP và private_key ký HMAC đều xử lý bằng WebCrypto, site này không thấy bí mật nào
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

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
    headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' {apiOrigin}".TrimEnd() + "; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
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

app.MapRazorPages();

app.Run();
