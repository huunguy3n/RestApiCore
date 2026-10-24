using System.Net;
using System.Text.RegularExpressions;
using ApiDocs.ControlPlane;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Models;
using Security;
using FakeStore = Tests.ControlPlaneFakeStore;

namespace Tests
{
    /// <summary>
    /// Control plane (portal trung tâm): băm mật khẩu, quyền theo dự án (khách chỉ thấy dự án của mình, staff theo membership),
    /// khóa đăng nhập, và luồng trang /Admin (CSRF, open redirect, cookie, header)
    /// </summary>
    public sealed class ControlPlaneTests
    {
        // ---------- Băm mật khẩu ----------
        [Fact]
        public void Hasher_verifies_only_the_right_password_and_never_repeats_a_hash()
        {
            string hash = PortalPasswordHasher.Hash("MatKhau-Dai-12345", iterations: 20_000);
            Assert.True(PortalPasswordHasher.Verify("MatKhau-Dai-12345", hash));
            Assert.False(PortalPasswordHasher.Verify("matkhau-dai-12345", hash));
            Assert.NotEqual(hash, PortalPasswordHasher.Hash("MatKhau-Dai-12345", iterations: 20_000));
            Assert.DoesNotContain("MatKhau", hash);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("plaintext")]
        [InlineData("v1.20000.AAAA.BBBB")]
        [InlineData("v1.1.QUJDREVGR0hJSktMTU5PUA==.QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=")]
        [InlineData("v2.20000.QUJDREVGR0hJSktMTU5PUA==.QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=")]
        [InlineData("v1.20000.@@@.@@@")]
        public void Hasher_rejects_malformed_stored_values(string? stored)
        {
            Assert.False(PortalPasswordHasher.Verify("bat-ky", stored));
        }

        // ---------- Quyền theo dự án ----------
        private static readonly ProjectInfo ProjectA = new(1, "A", "Dự án A");
        private static readonly ProjectInfo ProjectB = new(2, "B", "Dự án B");

        private static FakeStore Store() => new()
        {
            Projects = [ProjectA, ProjectB],
            Permissions =
            {
                ["partner"] = [PortalPermissions.FormsRead],
                ["deployment"] = [PortalPermissions.FormsRead, PortalPermissions.FormsEdit, PortalPermissions.FormsPermissionsManage],
                ["administrator"] = [PortalPermissions.UsersRead, PortalPermissions.UsersCreate, PortalPermissions.FormsEdit, PortalPermissions.AuditRead, PortalPermissions.FormsRead, PortalPermissions.FormsPublish, "khong.co.that"]
            }
        };

        private static MembershipRow Member(int account, int? project, string role, string scope, string? apiUser = null) =>
            new(account * 100 + (project ?? 0), account, project, role, scope, apiUser);

        [Fact]
        public async Task Customer_sees_only_own_project_with_partner_permissions()
        {
            FakeStore store = Store();
            store.Accounts[1] = new PortalAccount(1, "khach1", "Khách 1", AccountKind.Customer, true, "x");
            store.Memberships[1] = [Member(1, 1, "partner", "customer", "KHACH1_API")];

            AccountAccess access = (await new ProjectAccessService(store).GetAccessAsync(1))!;

            ProjectGrant grant = Assert.Single(access.Projects);
            Assert.Equal("A", grant.Project.Code);
            Assert.Equal("KHACH1_API", grant.ProjectUserName);
            Assert.True(access.Can(1, PortalPermissions.FormsRead));
            Assert.False(access.Can(2, PortalPermissions.FormsRead));
            Assert.False(access.Can(1, PortalPermissions.FormsEdit));
            Assert.False(access.Can(1, PortalPermissions.UsersRead));
        }

        [Fact]
        public async Task Customer_cannot_gain_staff_roles_or_all_project_scope_even_if_db_rows_say_so()
        {
            FakeStore store = Store();
            store.Accounts[1] = new PortalAccount(1, "khach1", "Khách 1", AccountKind.Customer, true, "x");
            store.Memberships[1] =
            [
                Member(1, null, "partner", "customer"),            // khách không dự án
                Member(1, 1, "deployment", "staff"),               // khách mang vai trò staff
                Member(1, null, "administrator", "staff"),         // khách thành administrator mọi dự án
                Member(1, 2, "administrator", "customer")          // administrator không phải vai trò phạm vi customer
            ];

            AccountAccess access = (await new ProjectAccessService(store).GetAccessAsync(1))!;

            Assert.Empty(access.Projects);
        }

        [Fact]
        public async Task Staff_permissions_follow_membership_and_deployment_has_no_user_management()
        {
            FakeStore store = Store();
            store.Accounts[2] = new PortalAccount(2, "nv", "Nhân viên", AccountKind.Staff, true, "x");
            store.Memberships[2] = [Member(2, 1, "deployment", "staff"), Member(2, null, "deployment", "staff")];

            AccountAccess access = (await new ProjectAccessService(store).GetAccessAsync(2))!;

            Assert.True(access.Can(1, PortalPermissions.FormsEdit));
            Assert.True(access.Can(1, PortalPermissions.FormsPermissionsManage));
            Assert.False(access.Can(1, PortalPermissions.UsersCreate));
            Assert.False(access.Can(1, PortalPermissions.UsersRead));
            Assert.False(access.Can(2, PortalPermissions.FormsEdit));   // deployment không dự án là dòng sai, bị bỏ qua
            Assert.Null(access.For(2));
        }

        [Fact]
        public async Task Central_administrator_covers_active_projects_and_unknown_permissions_are_ignored()
        {
            FakeStore store = Store();
            store.Accounts[3] = new PortalAccount(3, "admin", "Admin", AccountKind.Staff, true, "x");
            store.Memberships[3] = [Member(3, null, "administrator", "staff")];

            AccountAccess access = (await new ProjectAccessService(store).GetAccessAsync(3))!;

            Assert.Equal(2, access.Projects.Count);
            Assert.True(access.Can(1, PortalPermissions.UsersCreate) && access.Can(2, PortalPermissions.UsersCreate));
            Assert.DoesNotContain("khong.co.that", access.Projects.SelectMany(p => p.Permissions));
        }

        [Fact]
        public async Task Locked_accounts_and_inactive_projects_have_no_access_and_denials_are_audited()
        {
            FakeStore store = Store();
            store.Projects = [ProjectA];   // B ngừng hoạt động
            store.Accounts[3] = new PortalAccount(3, "admin", "Admin", AccountKind.Staff, true, "x");
            store.Memberships[3] = [Member(3, null, "administrator", "staff")];
            store.Accounts[4] = new PortalAccount(4, "khoa", "Bị khóa", AccountKind.Staff, false, "x");
            store.Memberships[4] = [Member(4, null, "administrator", "staff")];
            ProjectAccessService service = new(store);

            Assert.Null(await service.GetAccessAsync(4));
            Assert.Null(await service.GetAccessAsync(99));
            Assert.Single((await service.GetAccessAsync(3))!.Projects);

            Assert.False(await service.AuthorizeAsync(3, 2, PortalPermissions.UsersRead, "users.list"));
            Assert.True(await service.AuthorizeAsync(3, 1, PortalPermissions.UsersRead, "users.list"));
            AuditRecord denied = Assert.Single(store.Audit);
            Assert.Equal(("DENIED", 2, 3), (denied.Outcome, denied.ProjectId, denied.ActorAccountId));
        }

        // ---------- Đăng nhập ----------
        private sealed class FakeTime(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private static FakeStore LoginStore()
        {
            FakeStore store = Store();
            string hash = PortalPasswordHasher.Hash("MatKhau-Dai-12345", iterations: 20_000);
            store.Accounts[1] = new PortalAccount(1, "nv1", "Nhân viên", AccountKind.Staff, true, hash);
            store.Accounts[2] = new PortalAccount(2, "nghi", "Nghỉ việc", AccountKind.Staff, false, hash);
            return store;
        }

        [Fact]
        public async Task Login_uses_one_error_for_wrong_password_unknown_and_inactive_accounts()
        {
            FakeStore store = LoginStore();
            AccountLoginService login = new(store, TimeProvider.System);

            Assert.Equal("SUCCESS", (await login.LoginAsync("nv1", "MatKhau-Dai-12345")).Code);
            string[] codes =
            [
                (await login.LoginAsync("nv1", "sai-mat-khau")).Code,
                (await login.LoginAsync("khong-ton-tai", "MatKhau-Dai-12345")).Code,
                (await login.LoginAsync("nghi", "MatKhau-Dai-12345")).Code,
                (await login.LoginAsync("", "x")).Code
            ];
            Assert.All(codes, code => Assert.Equal("INVALID_CREDENTIAL", code));
            Assert.DoesNotContain(store.Audit, a => (a.Target ?? "").Contains("MatKhau") || (a.Detail ?? "").Contains("MatKhau"));
        }

        [Fact]
        public async Task Login_locks_after_repeated_failures_even_for_unknown_names_and_unlocks_later()
        {
            FakeStore store = LoginStore();
            FakeTime time = new(DateTimeOffset.UtcNow);
            AccountLoginService login = new(store, time);

            for (int i = 0; i < AccountLoginService.MaxFailures; i++)
            {
                await login.LoginAsync("nv1", "sai");
                await login.LoginAsync("khong-ton-tai", "sai");
            }

            Assert.Equal("ACCOUNT_LOCKED", (await login.LoginAsync("nv1", "MatKhau-Dai-12345")).Code);
            Assert.Equal("ACCOUNT_LOCKED", (await login.LoginAsync("khong-ton-tai", "sai")).Code);

            time.Now += AccountLoginService.LockFor + TimeSpan.FromSeconds(1);
            Assert.Equal("SUCCESS", (await login.LoginAsync("nv1", "MatKhau-Dai-12345")).Code);
        }

        // ---------- Luồng trang /Admin ----------
        private sealed class AdminFactory(FakeStore store) : WebApplicationFactory<AccountLoginService>
        {
            protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
                builder.UseEnvironment("Development").ConfigureServices(services =>
                {
                    services.RemoveAll<IControlPlaneStore>();
                    services.AddSingleton<IControlPlaneStore>(store);
                });
        }

        private static async Task<(string Token, HttpResponseMessage Page)> LoginFormAsync(HttpClient http, string url = "/Admin/Login")
        {
            HttpResponseMessage page = await http.GetAsync(url);
            string html = await page.Content.ReadAsStringAsync();
            Match token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            return (token.Success ? token.Groups[1].Value : "", page);
        }

        private static FormUrlEncodedContent Form(string token, string login, string password, string returnUrl = "") =>
            new(new Dictionary<string, string> { ["Login"] = login, ["Password"] = password, ["returnUrl"] = returnUrl, ["__RequestVerificationToken"] = token });

        [Fact]
        public async Task Admin_requires_login_and_login_page_is_hardened()
        {
            using AdminFactory factory = new(LoginStore());
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            HttpResponseMessage anonymous = await http.GetAsync("/Admin");
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith("/Admin/Login", anonymous.Headers.Location!.OriginalString.Replace("http://localhost", ""), StringComparison.Ordinal);

            var (_, page) = await LoginFormAsync(http);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            string csp = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("form-action 'self'", csp);
            Assert.Contains("script-src 'self'", csp);
            Assert.Contains("no-store", string.Join(",", page.Headers.GetValues("Cache-Control")));

            // Cổng đối tác tĩnh giữ nguyên CSP chặn form
            HttpResponseMessage root = await http.GetAsync("/");
            Assert.Contains("form-action 'none'", string.Join(";", root.Headers.GetValues("Content-Security-Policy")));
        }

        [Fact]
        public async Task Login_post_without_antiforgery_token_is_rejected()
        {
            using AdminFactory factory = new(LoginStore());
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await LoginFormAsync(http);

            HttpResponseMessage response = await http.PostAsync("/Admin/Login", Form("", "nv1", "MatKhau-Dai-12345"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie") && response.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("fast-portal=")));
        }

        [Fact]
        public async Task Successful_login_sets_a_strict_httponly_cookie_and_ignores_external_return_urls()
        {
            FakeStore store = LoginStore();
            store.Memberships[1] = [Member(1, 1, "deployment", "staff")];
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var (token, _) = await LoginFormAsync(http);
            HttpResponseMessage login = await http.PostAsync("/Admin/Login", Form(token, "nv1", "MatKhau-Dai-12345", "https://evil.example/steal"));

            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Equal("/Admin", login.Headers.Location!.OriginalString);
            string cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fast-portal="));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

            string dashboard = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin")).Content.ReadAsStringAsync());
            Assert.Contains("Dự án A", dashboard);
            Assert.Contains(PortalPermissions.FormsEdit, dashboard);
            Assert.DoesNotContain("Dự án B", dashboard);
            Assert.DoesNotContain(PortalPermissions.UsersCreate, dashboard);
        }

        [Fact]
        public async Task Wrong_password_shows_generic_message_and_revoked_access_ends_the_session()
        {
            FakeStore store = LoginStore();
            store.Memberships[1] = [Member(1, 1, "deployment", "staff")];
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var (token, _) = await LoginFormAsync(http);
            string failed = WebUtility.HtmlDecode(await (await http.PostAsync("/Admin/Login", Form(token, "nv1", "sai-mat-khau"))).Content.ReadAsStringAsync());
            Assert.Contains("Tài khoản hoặc mật khẩu không đúng", failed);

            var (token2, _) = await LoginFormAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, (await http.PostAsync("/Admin/Login", Form(token2, "nv1", "MatKhau-Dai-12345"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/Admin")).StatusCode);

            // Khóa tài khoản trong DB: cookie cũ không còn giá trị ở request kế tiếp
            store.Accounts[1] = store.Accounts[1] with { Active = false };
            HttpResponseMessage after = await http.GetAsync("/Admin");
            Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
            Assert.Contains("/Admin/Login", after.Headers.Location!.OriginalString);
        }

        // ---------- Trang quản trị tài khoản ----------
        private static async Task SignInAsync(HttpClient http, string login, string password)
        {
            var (token, _) = await LoginFormAsync(http);
            HttpResponseMessage response = await http.PostAsync("/Admin/Login", Form(token, login, password));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        private static FakeStore AdminStore()
        {
            FakeStore store = LoginStore();
            store.Memberships[1] = [Member(1, null, "administrator", "staff")];
            store.Accounts[3] = new PortalAccount(3, "khach1", "Khách một", AccountKind.Customer, true, PortalPasswordHasher.Hash("MatKhau-Dai-12345", iterations: 20_000));
            store.Memberships[3] = [Member(3, 1, "partner", "customer")];
            return store;
        }

        [Fact]
        public async Task Central_administrator_sees_accounts_menu_and_list_but_posts_need_antiforgery()
        {
            FakeStore store = AdminStore();
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(http, "nv1", "MatKhau-Dai-12345");

            string home = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin")).Content.ReadAsStringAsync());
            Assert.Contains("/Admin/Accounts", home);

            string list = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin/Accounts")).Content.ReadAsStringAsync());
            Assert.Contains("khach1", list);
            Assert.DoesNotContain("v1.", list);   // không lộ băm mật khẩu

            HttpResponseMessage noToken = await http.PostAsync("/Admin/Accounts/Create", Form("", "khach_moi", "MatKhau-Dai-12345"));
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            Assert.DoesNotContain(store.Accounts.Values, a => a.Login == "khach_moi");
        }

        [Fact]
        public async Task Customer_gets_no_accounts_menu_and_cannot_create_accounts_even_with_a_valid_token()
        {
            FakeStore store = AdminStore();
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(http, "khach1", "MatKhau-Dai-12345");

            string home = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin")).Content.ReadAsStringAsync());
            Assert.DoesNotContain("/Admin/Accounts", home);
            Assert.DoesNotContain("/Admin/Projects", home);

            string list = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin/Accounts")).Content.ReadAsStringAsync());
            Assert.Contains("không có quyền", list);
            Assert.DoesNotContain("nv1", list);

            var (token, _) = await LoginFormAsync(http, "/Admin/Password");
            int before = store.Accounts.Count;
            HttpResponseMessage create = await http.PostAsync("/Admin/Accounts/Create", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Login"] = "leo_thang", ["Input.DisplayName"] = "X", ["Input.Kind"] = "staff", ["Input.RoleCode"] = "administrator",
                ["Input.Password"] = "MatKhau-Dai-12345", ["__RequestVerificationToken"] = token
            }));
            Assert.NotEqual(HttpStatusCode.Redirect, create.StatusCode);
            Assert.Equal(before, store.Accounts.Count);

            HttpResponseMessage projects = await http.GetAsync("/Admin/Projects");
            Assert.Contains("không có quyền", WebUtility.HtmlDecode(await projects.Content.ReadAsStringAsync()));
            HttpResponseMessage detail = await http.GetAsync("/Admin/Accounts/Detail/1");
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        }

        // ---------- Audit và dashboard trên trang ----------
        [Fact]
        public async Task Central_administrator_gets_dashboard_numbers_and_the_audit_page()
        {
            FakeStore store = AdminStore();
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(http, "nv1", "MatKhau-Dai-12345");   // đăng nhập này đã ghi một dòng audit "login"

            string home = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin")).Content.ReadAsStringAsync());
            Assert.Contains("Khách hàng hoạt động", home);
            Assert.Contains("Hoạt động gần đây", home);
            Assert.Contains("/Admin/Audit", home);

            string audit = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin/Audit?action=login")).Content.ReadAsStringAsync());
            Assert.Contains("nv1", audit);
            Assert.Contains("SUCCESS", audit);
            Assert.DoesNotContain("v1.", audit);   // không lộ băm mật khẩu

            HttpResponseMessage bad = await http.GetAsync("/Admin/Audit?outcome=HACK");
            Assert.Contains("chưa hợp lệ", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
        }

        [Fact]
        public async Task Customer_sees_no_audit_menu_page_or_dashboard_numbers()
        {
            FakeStore store = AdminStore();
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(http, "khach1", "MatKhau-Dai-12345");

            string home = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin")).Content.ReadAsStringAsync());
            Assert.DoesNotContain("/Admin/Audit", home);
            Assert.DoesNotContain("Hoạt động gần đây", home);
            Assert.DoesNotContain("Nhân viên hoạt động", home);

            string audit = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin/Audit")).Content.ReadAsStringAsync());
            Assert.Contains("không có quyền", audit);
            Assert.DoesNotContain("nv1", audit);
        }

        // ---------- Form editor, gói, đồng bộ trên trang ----------
        private static async Task<string> TokenFromAsync(HttpClient http, string url)
        {
            var (token, page) = await LoginFormAsync(http, url);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            return token;
        }

        [Fact]
        public async Task Staff_edits_publishes_and_downloads_a_package_through_the_pages()
        {
            FakeStore store = AdminStore();
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(http, "nv1", "MatKhau-Dai-12345");

            string home = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin")).Content.ReadAsStringAsync());
            Assert.Contains("/Admin/Forms", home);

            string editUrl = "/Admin/Forms/Edit?project=1&form=getCustomer";
            string token = await TokenFromAsync(http, editUrl);
            string template = ApiDocs.ControlPlane.FormManagementService.NewFormTemplate;
            FormUrlEncodedContent Body(string t) => new(new Dictionary<string, string> { ["content"] = template, ["baseRevision"] = "", ["__RequestVerificationToken"] = t });

            // Không có antiforgery token: bị từ chối, không lưu gì
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync(editUrl.Replace("Edit?", "Edit?handler=Save&"), Body(""))).StatusCode);
            Assert.Empty(store.Revisions);

            HttpResponseMessage saved = await http.PostAsync(editUrl.Replace("Edit?", "Edit?handler=Save&"), Body(token));
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            Assert.Equal("validated", Assert.Single(store.Revisions).Status);

            string page = WebUtility.HtmlDecode(await (await http.GetAsync(editUrl)).Content.ReadAsStringAsync());
            Assert.Contains("Lịch sử revision", page);
            token = await TokenFromAsync(http, editUrl);
            HttpResponseMessage published = await http.PostAsync(editUrl.Replace("Edit?", "Edit?handler=Publish&"), new FormUrlEncodedContent(new Dictionary<string, string> { ["revision"] = "1", ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.Redirect, published.StatusCode);
            Assert.Equal("published", store.Revisions[0].Status);

            token = await TokenFromAsync(http, "/Admin/Packages?project=1");
            HttpResponseMessage created = await http.PostAsync("/Admin/Packages?handler=Create&project=1", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            Assert.Single(store.Packages);

            HttpResponseMessage download = await http.GetAsync("/Admin/Packages?handler=Download&project=1&id=1");
            Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Business.PackageContents contents = Business.FormPackage.Read(await download.Content.ReadAsByteArrayAsync());
            Assert.Equal("getCustomer", Assert.Single(contents.Manifest.Forms).Name);

            string sync = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin/Sync")).Content.ReadAsStringAsync());
            Assert.Contains("EXPORT", sync);
        }

        [Fact]
        public async Task Customer_cannot_open_the_form_editor_save_or_download_even_with_a_valid_token()
        {
            FakeStore store = AdminStore();
            await store.AddRevisionAsync(1, "getCustomer", null, ApiDocs.ControlPlane.FormManagementService.NewFormTemplate, "x", "published", 1);
            using AdminFactory factory = new(store);
            using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInAsync(http, "khach1", "MatKhau-Dai-12345");

            string home = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin")).Content.ReadAsStringAsync());
            Assert.DoesNotContain("/Admin/Forms", home);
            Assert.DoesNotContain("/Admin/Packages", home);

            string edit = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin/Forms/Edit?project=1&form=getCustomer")).Content.ReadAsStringAsync());
            Assert.Contains("không có quyền", edit);
            Assert.DoesNotContain("SELECT", edit);   // không lộ SQL trong form

            string token = await TokenFromAsync(http, "/Admin/Password");
            HttpResponseMessage save = await http.PostAsync("/Admin/Forms/Edit?handler=Save&project=1&form=getCustomer", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["content"] = ApiDocs.ControlPlane.FormManagementService.NewFormTemplate, ["baseRevision"] = "1", ["__RequestVerificationToken"] = token
            }));
            Assert.NotEqual(HttpStatusCode.Redirect, save.StatusCode);
            Assert.Single(store.Revisions);

            HttpResponseMessage download = await http.GetAsync("/Admin/Packages?handler=Download&project=1&id=1");
            Assert.NotEqual("application/zip", download.Content.Headers.ContentType?.MediaType);
            string diff = WebUtility.HtmlDecode(await (await http.GetAsync("/Admin/Forms/Diff?project=1&form=getCustomer&from=1&to=1")).Content.ReadAsStringAsync());
            Assert.Contains("không có quyền", diff);
            Assert.DoesNotContain("SELECT", diff);
        }

        // ---------- Cấu hình ----------
        [Fact]
        public void Connection_string_comes_from_ControlPlane_first_then_from_the_api_style_key()
        {
            static IConfiguration Config(params (string Key, string Value)[] values) =>
                new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

            Assert.Equal("", ApiDocs.ControlPlane.ControlPlaneConfig.ConnectionString(Config()));
            Assert.Equal("A", ApiDocs.ControlPlane.ControlPlaneConfig.ConnectionString(Config(("connectionStrings:appConnectionString", " A "))));
            Assert.Equal("B", ApiDocs.ControlPlane.ControlPlaneConfig.ConnectionString(Config(("ControlPlane:ConnectionString", "B"), ("connectionStrings:appConnectionString", "A"))));
            Assert.Equal("A", ApiDocs.ControlPlane.ControlPlaneConfig.ConnectionString(Config(("ControlPlane:ConnectionString", "  "), ("connectionStrings:appConnectionString", "A"))));
        }
    }
}
