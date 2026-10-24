using System.Net;
using System.Text.RegularExpressions;
using WebPortal.ControlPlane;
using Business;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Models;
using Security;

namespace Tests
{
    /// <summary>Các lỗi tìm thấy khi rà soát bảo mật portal /Admin, form editor, gói và Cli apply-package, mỗi lỗi một test hồi quy</summary>
    public sealed class PortalSecurityReviewTests
    {
        private const string Password = "MatKhau-Dai-12345";

        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static ControlPlaneFakeStore Store()
        {
            ControlPlaneFakeStore store = new() { Projects = [new ProjectInfo(1, "A", "Dự án A")] };
            store.AllProjects.Add(new ProjectDetail(1, "A", "Dự án A", "https://a.example", true));
            store.Permissions["administrator"] = [PortalPermissions.UsersRead, PortalPermissions.UsersCreate, PortalPermissions.UsersRolesManage, PortalPermissions.UsersDeactivate, PortalPermissions.FormsRead];
            store.Permissions["partner"] = [PortalPermissions.FormsRead];
            string hash = PortalPasswordHasher.Hash(Password, iterations: 20_000);
            store.Accounts[1] = new PortalAccount(1, "admin", "Quản trị", AccountKind.Staff, true, hash);
            store.Memberships[1] = [new MembershipRow(1, 1, null, "administrator", "staff", null)];
            store.Accounts[2] = new PortalAccount(2, "khach", "Khách", AccountKind.Customer, true, hash);
            store.Memberships[2] = [new MembershipRow(2, 2, 1, "partner", "customer", "KHACH_API")];
            store.Accounts[3] = new PortalAccount(3, "khach_hai", "Khách hai", AccountKind.Customer, true, hash);
            store.Memberships[3] = [new MembershipRow(3, 3, 1, "partner", "customer", null)];
            return store;
        }

        // ---------- Đăng nhập: không khóa hộ được tài khoản người khác từ một IP ----------
        [Fact]
        public async Task One_address_cannot_lock_an_account_for_everyone_else()
        {
            AccountLoginService login = new(Store(), TimeProvider.System);

            for (int i = 0; i < AccountLoginService.MaxFailures; i++)
            {
                await login.LoginAsync("admin", "sai-mat-khau", "6.6.6.6");
            }

            Assert.Equal("ACCOUNT_LOCKED", (await login.LoginAsync("admin", Password, "6.6.6.6")).Code);   // IP tấn công bị khóa
            Assert.Equal("SUCCESS", (await login.LoginAsync("admin", Password, "10.0.0.5")).Code);          // chủ tài khoản từ IP khác vẫn vào được
        }

        [Fact]
        public async Task A_distributed_guessing_campaign_still_locks_the_account_globally_for_a_while()
        {
            FixedTime time = new(DateTimeOffset.UtcNow);
            AccountLoginService login = new(Store(), time);

            for (int i = 0; i < AccountLoginService.GlobalMaxFailures; i++)
            {
                await login.LoginAsync("admin", "sai-mat-khau", $"7.7.{i / 200}.{i % 200}");   // mỗi lần một IP khác
            }

            Assert.Equal("ACCOUNT_LOCKED", (await login.LoginAsync("admin", Password, "10.0.0.5")).Code);
            time.Now += AccountLoginService.LockFor + TimeSpan.FromSeconds(1);
            Assert.Equal("SUCCESS", (await login.LoginAsync("admin", Password, "10.0.0.5")).Code);
        }

        [Fact]
        public async Task Control_characters_in_a_login_attempt_cannot_forge_audit_lines()
        {
            ControlPlaneFakeStore store = Store();
            AccountLoginService login = new(store, TimeProvider.System);

            await login.LoginAsync("ke\ntan\rcong\u0000", "sai-mat-khau", "6.6.6.6");

            AuditRecord entry = Assert.Single(store.Audit);
            Assert.Equal("ketancong", entry.Target);
        }

        // ---------- Phiên: đổi mật khẩu / khóa cắt phiên cũ ----------
        private sealed class Factory(ControlPlaneFakeStore store, string environment = "Development") : WebApplicationFactory<AccountLoginService>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder) =>
                builder.UseEnvironment(environment).ConfigureServices(services =>
                {
                    services.RemoveAll<IControlPlaneStore>();
                    services.AddSingleton<IControlPlaneStore>(store);
                });
        }

        private static async Task<string> TokenAsync(HttpClient http, string url)
        {
            string html = await (await http.GetAsync(url)).Content.ReadAsStringAsync();
            Match token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(token.Success, "không có antiforgery token ở " + url);
            return token.Groups[1].Value;
        }

        private static async Task<HttpResponseMessage> SignInAsync(HttpClient http, string login)
        {
            string token = await TokenAsync(http, "/Admin/Login");
            return await http.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Login"] = login, ["Password"] = Password, ["__RequestVerificationToken"] = token }));
        }

        private static HttpClient Client(Factory factory, string address = "http://localhost") =>
            factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(address) });

        [Fact]
        public async Task Changing_the_password_ends_every_other_session_of_that_account()
        {
            ControlPlaneFakeStore store = Store();
            using Factory factory = new(store);
            using HttpClient stolen = Client(factory), owner = Client(factory);
            Assert.Equal(HttpStatusCode.Redirect, (await SignInAsync(stolen, "admin")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await SignInAsync(owner, "admin")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await stolen.GetAsync("/Admin")).StatusCode);

            string token = await TokenAsync(owner, "/Admin/Password");
            HttpResponseMessage changed = await owner.PostAsync("/Admin/Password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["current"] = Password, ["replacement"] = "MatKhauMoi-Dai-98765", ["__RequestVerificationToken"] = token
            }));

            Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
            Assert.Contains("/Admin/Login", changed.Headers.Location!.OriginalString);
            HttpResponseMessage replay = await stolen.GetAsync("/Admin");   // phiên cũ (bị đánh cắp) không còn dùng được
            Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
            Assert.Contains("/Admin/Login", replay.Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.Redirect, (await owner.GetAsync("/Admin")).StatusCode);   // chủ tài khoản cũng phải đăng nhập lại
        }

        [Fact]
        public async Task The_login_page_is_rate_limited_per_address()
        {
            using Factory factory = new(Store());
            using HttpClient http = Client(factory);

            List<HttpStatusCode> codes = [];
            for (int i = 0; i < 25; i++)
            {
                codes.Add((await http.GetAsync("/Admin/Login")).StatusCode);
            }

            Assert.Equal(HttpStatusCode.OK, codes[0]);
            Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        }

        [Fact]
        public async Task Outside_development_cookies_are_secure_host_prefixed_and_strict()
        {
            using Factory factory = new(Store(), "Production");
            using HttpClient http = Client(factory, "https://localhost");

            HttpResponseMessage login = await SignInAsync(http, "admin");

            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            string cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-fast-portal="));
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/Admin")).StatusCode);
        }

        // ---------- Đổi mật khẩu: không dò được mật khẩu hiện tại bằng phiên bị đánh cắp ----------
        [Fact]
        public async Task Password_change_locks_after_repeated_wrong_current_passwords()
        {
            FixedTime time = new(DateTimeOffset.UtcNow);
            ControlPlaneFakeStore store = Store();
            AccountAdminService service = new(store, new ProjectAccessService(store), time);

            for (int i = 0; i < AccountAdminService.MaxPasswordFailures; i++)
            {
                Assert.Equal("INVALID_CREDENTIAL", (await service.ChangeOwnPasswordAsync(2, "doan-mo", "MatKhauMoi-Dai-98765")).Code);
            }

            Assert.Equal("TOO_MANY_ATTEMPTS", (await service.ChangeOwnPasswordAsync(2, Password, "MatKhauMoi-Dai-98765")).Code);   // đúng mật khẩu cũng bị chặn trong lúc khóa
            Assert.True(PortalPasswordHasher.Verify(Password, store.Accounts[2].PasswordHash));

            time.Now += AccountAdminService.PasswordLockFor + TimeSpan.FromSeconds(1);
            Assert.True((await service.ChangeOwnPasswordAsync(2, Password, "MatKhauMoi-Dai-98765")).Success);
        }

        // ---------- Tài khoản API không gắn trùng ----------
        [Fact]
        public async Task A_project_api_account_can_belong_to_only_one_portal_account()
        {
            ControlPlaneFakeStore store = Store();
            AccountAdminService service = new(store, new ProjectAccessService(store));

            // Khách hai bị gắn với danh tính API đã thuộc về khách một
            Assert.Equal("DUPLICATE", (await service.AddMembershipAsync(1, 3, 1, "partner", "khach_api")).Code);
            Assert.Equal("DUPLICATE", (await service.CreateAccountAsync(1, "khach_ba", "Khách ba", "customer", "MatKhau-Dai-12345", 1, "partner", "KHACH_API")).Code);
            Assert.DoesNotContain(store.Accounts.Values, a => a.Login == "khach_ba");
        }

        // ---------- Nhập kết quả đồng bộ: không giả mạo được dòng nhật ký ----------
        [Fact]
        public async Task Control_characters_in_an_imported_result_cannot_forge_sync_log_lines()
        {
            ControlPlaneFakeStore store = Store();
            store.Permissions["administrator"] = [.. store.Permissions["administrator"], PortalPermissions.FormsEdit, PortalPermissions.FormsPublish];
            FormManagementService service = new(store, new ProjectAccessService(store), TimeProvider.System);
            string form = FormDefinitionValidator.Normalize(FormManagementService.NewFormTemplate);
            await service.PublishAsync(1, 1, "getCustomer", ((SaveResult)(await service.SaveAsync(1, 1, "getCustomer", null, form)).Data!).Revision);
            int packageId = (int)(await service.CreatePackageAsync(1, 1)).Data!;
            string sha = store.Packages.Single().Sha;

            string json = $$"""
                {"packageId": {{packageId}}, "project": "A", "manifestSha256": "{{sha}}", "environment": "PROD", "operator": "nguyen\n2026-01-01 FAKE", "appliedAt": "x",
                 "applied": true, "success": false, "files": [{"name": "getCustomer", "revision": 1, "sha256": "x", "outcome": "FAILED", "message": "loi\n[FAKE] Admin da duyet"}]}
                """;

            Assert.True((await service.RecordApplyResultAsync(1, 1, json)).Success);

            SyncLogEntry entry = store.SyncLog.Last();
            Assert.DoesNotContain('\n', entry.Detail!);
            Assert.DoesNotContain('\n', entry.Actor!);
            Assert.Equal("FAILED", entry.Outcome);
        }

        // ---------- Cli apply-package: gõ nhầm thư mục đích ----------
        [Fact]
        public void Applying_to_a_missing_target_directory_is_refused_but_a_dry_run_still_works()
        {
            string form = FormDefinitionValidator.Normalize(FormManagementService.NewFormTemplate);
            var (zip, sha) = FormPackage.Build("A", 1, DateTime.UtcNow, [("getCustomer", 1, form)]);
            string missing = Path.Combine(Path.GetTempPath(), "khong-co-" + Guid.NewGuid().ToString("N"));

            Assert.True(FormPackageApplier.Run(zip, sha, missing, apply: false, "PROD", "nguyen", TimeProvider.System).Success);
            FormPackageException ex = Assert.Throws<FormPackageException>(() => FormPackageApplier.Run(zip, sha, missing, apply: true, "PROD", "nguyen", TimeProvider.System));

            Assert.Equal("TARGET_NOT_FOUND", ex.Code);
            Assert.False(Directory.Exists(missing));
        }
    }
}
