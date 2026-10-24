using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPortal.Portal;

namespace WebPortal.Pages
{
    public sealed record NavItem(string Href, string Text, string Key);

    /// <summary>
    /// Nền chung: danh tính đang đăng nhập (dựng lại mỗi request từ cookie + phiên), thông báo qua TempData, dịch mã lỗi, menu.
    /// Mỗi thao tác vẫn tự kiểm quyền ở service; menu chỉ để dễ dùng
    /// </summary>
    [RequestSizeLimit(1_048_576)]
    public abstract class PortalPageModel : PageModel
    {
        public PortalIdentity? Identity { get; protected set; }

        [TempData] public string? Flash { get; set; }
        [TempData] public string? FlashError { get; set; }

        public virtual IReadOnlyList<NavItem> Nav => [];
        public virtual string NavKey => "";
        public virtual string Brand => "Fast API Portal";
        public virtual string? NoticeUrl => null;
        public virtual string LogoutUrl => "";
        public virtual string PasswordUrl => "";

        protected string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
        protected CancellationToken Aborted => HttpContext.RequestAborted;

        public static string Describe(string code) => code switch
        {
            "FORBIDDEN" => "Bạn không có quyền thực hiện thao tác này.",
            "NOT_FOUND" => "Không tìm thấy đối tượng.",
            "INVALID_INPUT" => "Dữ liệu chưa hợp lệ (tên đăng nhập 3-32 ký tự chữ, số, . _ @ -; mật khẩu tối thiểu 12 ký tự).",
            "DUPLICATE" => "Tên đăng nhập hoặc mã đã được dùng (tên đăng nhập là duy nhất toàn portal).",
            "SCHEMA_MISSING" => "PortalDb chưa có đủ bảng. Chạy sql/portal/001_portalDb.sql rồi 002_portalCustomize.sql trên DB trong Portal:ConnectionString.",
            "INVALID_CREDENTIAL" => "Mật khẩu không đúng.",
            "ACCOUNT_LOCKED" => "Tạm khóa do nhập sai nhiều lần. Thử lại sau ít phút.",
            "INVALID_FORM_NAME" => "Tên form không hợp lệ (tên file XML không đuôi, vd getCustomer).",
            "INVALID_IP" => "IP không hợp lệ (mỗi dòng một IP, \"255\" là wildcard, có thể kèm mô tả sau dấu cách).",
            "ERP_USER_NOT_FOUND" => "Không có NSD FBO đang hoạt động với tên này (name_erp).",
            "PROJECT_UNREACHABLE" => "Không truy cập được folder/DB của dự án.",
            "SYNC_FAILED" => "Đã lưu trên portal nhưng chưa đồng bộ được xuống dự án. Bấm \"Đồng bộ lại\" khi dự án truy cập được.",
            "NOT_SYNCED" => "User chưa có trên DB dự án. Đồng bộ lại trước.",
            "KEY_NOT_ISSUED" => "Chưa được cấp private_key. Liên hệ Admin dự án.",
            "NO_KEY_ENCRYPTION_KEY" => "Folder dự án thiếu Security:KeyEncryptionKey trong appsettings.Local.json.",
            "INVALID_PROJECT_CODE" => "Mã dự án không hợp lệ hoặc trùng đường dẫn dành riêng của portal.",
            "QLDA_INACTIVE" => "Mã dự án không có trong QLDA hoặc nbdmda.status khác 1.",
            "INVALID_URL" => "Địa chỉ WebApi phải là http(s)://máy[:cổng], không kèm đường dẫn truy vấn.",
            "INVALID_FOLDER" => "Folder Api không hợp lệ.",
            "INVALID_RANGE" => "Khoảng thời gian không hợp lệ (tối đa 31 ngày).",
            "LICENSE_KEY_MISSING" => "Portal chưa có khóa ký license (SA cấu hình Portal:License).",
            "INVALID_LICENSE_REQUEST" => "File yêu cầu license không đúng định dạng (tạo bằng PortalApp).",
            "WRONG_PROJECT" => "File yêu cầu license thuộc dự án khác.",
            "INVALID_MANIFEST" => "integrity.json không đúng định dạng.",
            "NAME_CASE_CONFLICT" => "Đã có form cùng tên khác chữ hoa/thường (trên Windows là cùng một file).",
            "NO_FORMS" => "Chưa có form nào được publish.",
            "PROD_FOLDER" => "Folder này khai environment = PROD: áp gói PROD bằng PortalApp tại máy khách, không áp từ portal.",
            "PACKAGE_REJECTED" => "Gói không đạt kiểm tra, chưa ghi gì.",
            "PACKAGE_MISMATCH" => "File kết quả không khớp gói của dự án này.",
            "INTEGRITY" => "Gói không còn toàn vẹn so với bản đã lưu.",
            _ => "Không thực hiện được."
        };

        /// <summary>Ghi kết quả vào thông báo; trả true nếu thành công</summary>
        protected bool Report(OpResult result, string success)
        {
            if (result.Success)
            {
                Flash = success;
                return true;
            }
            FlashError = Describe(result.Code) + (result.Data is string detail && result.Code is "PROJECT_UNREACHABLE" or "SYNC_FAILED" or "INVALID_FOLDER" or "PACKAGE_REJECTED" ? " " + detail : "");
            return false;
        }

        /// <summary>Danh tính cho khu vực này; null = chưa đăng nhập (chuyển về trang đăng nhập)</summary>
        protected abstract Task<PortalIdentity?> ResolveIdentityAsync();

        protected abstract string LoginUrl { get; }

        protected virtual bool AllowAnonymous => false;

        public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            try
            {
                if (await PrepareAsync() is IActionResult early)
                {
                    context.Result = early;
                    return;
                }
                Identity = await ResolveIdentityAsync();
                if (Identity == null && !AllowAnonymous)
                {
                    string ended = HttpContext.Items[PortalAuth.RejectedSessionItem] is Guid sid ? "?ended=" + sid : "";
                    context.Result = Redirect(LoginUrl + ended);
                    return;
                }
                if (Identity is { IsApi: false } && !IsPasswordPage && !AllowAnonymous
                    && (await HttpContext.RequestServices.GetRequiredService<IPortalStore>().FindAccountAsync(Identity.AccountId, Aborted))?.MustChangePassword == true)
                {
                    context.Result = Redirect(PasswordUrl + "?required=1");
                    return;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Chưa cấu hình PortalDb / QLDA: hiện thông báo thay vì lỗi 500
                context.Result = Content("WebPortal chưa được cấu hình đầy đủ: " + ex.Message, "text/plain; charset=utf-8");
                return;
            }
            await next();
        }

        /// <summary>Chạy trước khi xác định danh tính (nạp dự án theo URL); trả kết quả để dừng sớm (404)</summary>
        protected virtual Task<IActionResult?> PrepareAsync() => Task.FromResult<IActionResult?>(null);

        protected virtual bool IsPasswordPage => false;

        /// <summary>Mở phiên đã xác nhận: cookie chỉ mang id phiên + dấu vết mật khẩu</summary>
        protected async Task SignInAsync(VerifiedAccount account, Guid sessionId, string scheme)
        {
            IPortalStore store = HttpContext.RequestServices.GetRequiredService<IPortalStore>();
            string hash = account.Kind == AccountKind.Api
                ? (await store.FindApiUserAsync(account.AccountId, Aborted))!.PasswordHash
                : (await store.FindAccountAsync(account.AccountId, Aborted))!.PasswordHash;
            await HttpContext.SignInAsync(scheme, PortalAuth.CreatePrincipal(account, sessionId, hash, scheme), new AuthenticationProperties { IsPersistent = false });
            await store.WriteAuditAsync(new AuditRecord(account.Kind, account.AccountId, account.Login, account.ProjectId, "login", account.Login, "SUCCESS",
                IpAddress: ClientIp, SessionId: sessionId), Aborted);
        }
    }

    /// <summary>/cpanel: chỉ SA</summary>
    public abstract class CpanelPageModel : PortalPageModel
    {
        protected override Task<PortalIdentity?> ResolveIdentityAsync() => PortalAuth.CpanelAsync(HttpContext);

        protected override string LoginUrl => "/cpanel/login";

        public override string Brand => "Fast API · Control Panel";
        public override string? NoticeUrl => "/cpanel/session/notice";
        public override string LogoutUrl => "/cpanel/logout";
        public override string PasswordUrl => "/cpanel/password";

        public override IReadOnlyList<NavItem> Nav =>
        [
            new("/cpanel", "Tổng quan", "overview"),
            new("/cpanel/projects", "Dự án", "projects"),
            new("/cpanel/admins", "Admin dự án", "admins"),
            new("/cpanel/licenses", "License", "licenses"),
            new("/cpanel/audit", "Nhật ký", "audit"),
            new("/cpanel/password", "Đổi mật khẩu", "password")
        ];
    }

    /// <summary>/{ma_da}: Admin của dự án, user Api của dự án, hoặc SA (thao tác thay Admin)</summary>
    public abstract class ProjectPageModel : PortalPageModel
    {
        [BindProperty(SupportsGet = true, Name = "maDa")] public string MaDa { get; set; } = "";

        public PortalProject Project { get; private set; } = null!;
        public string ProjectName { get; private set; } = "";

        public bool CanAdmin => PortalGuard.CanAdmin(Identity, Project.Id);
        public bool IsApi => PortalGuard.IsApiOf(Identity, Project.Id);

        protected override async Task<IActionResult?> PrepareAsync()
        {
            PortalLoginService login = HttpContext.RequestServices.GetRequiredService<PortalLoginService>();
            PortalProject? project = await login.FindOpenProjectAsync(MaDa, Aborted);
            if (project == null)
            {
                return NotFound();
            }
            Project = project;
            MaDa = project.MaDa;
            ProjectName = await HttpContext.RequestServices.GetRequiredService<ProjectService>().NameOfAsync(project.MaDa, Aborted) ?? project.MaDa;
            return null;
        }

        protected override Task<PortalIdentity?> ResolveIdentityAsync() => PortalAuth.ProjectAsync(HttpContext, MaDa);

        protected override string LoginUrl => $"/{MaDa}/login";

        public override string Brand => $"{ProjectName} · {MaDa}";
        public override string? NoticeUrl => Identity is { IsSuperAdmin: true } ? "/cpanel/session/notice" : $"/{MaDa}/session/notice";
        public override string LogoutUrl => Identity is { IsSuperAdmin: true } ? "/cpanel/logout" : $"/{MaDa}/logout";
        public override string PasswordUrl => Identity is { IsSuperAdmin: true } ? "/cpanel/password" : $"/{MaDa}/password";

        public override IReadOnlyList<NavItem> Nav => CanAdmin
            ?
            [
                new($"/{MaDa}", "Tổng quan", "overview"),
                new($"/{MaDa}/users", "User Api", "users"),
                new($"/{MaDa}/logs", "Log & hiệu suất", "logs"),
                new($"/{MaDa}/forms", "Khai báo form", "forms"),
                new($"/{MaDa}/packages", "Gói DEV→PROD", "packages"),
                new($"/{MaDa}/licenses", "License", "licenses"),
                new($"/{MaDa}/settings", "Cấu hình dự án", "settings"),
                .. Identity is { IsSuperAdmin: true } ? new NavItem[] { new("/cpanel", "← Control Panel", "cpanel") } : [new($"/{MaDa}/password", "Đổi mật khẩu", "password")]
            ]
            :
            [
                new($"/{MaDa}", "Hướng dẫn & form", "overview"),
                new($"/{MaDa}/errors", "Mã lỗi", "errors"),
                new($"/{MaDa}/key", "Khóa & token", "key"),
                new($"/{MaDa}/password", "Đổi mật khẩu", "password")
            ];

        /// <summary>403 cho thao tác ngoài quyền (vd user Api mở trang quản trị)</summary>
        protected IActionResult Denied() => new ContentResult { StatusCode = 403, Content = "Bạn không có quyền mở trang này.", ContentType = "text/plain; charset=utf-8" };
    }
}
