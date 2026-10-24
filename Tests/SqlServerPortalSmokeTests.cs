using System.Net;
using System.Text.RegularExpressions;
using WebPortal.ControlPlane;
using Business;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Models;
using Provider;
using Security;

namespace Tests
{
    /// <summary>
    /// Chạy portal thật (Razor Pages + cookie + antiforgery) trên DB SQL Server thật qua SqlControlPlaneStore, đi hết một vòng:
    /// đăng nhập, dashboard, dự án, tạo / khóa khách hàng, soạn - publish form, tạo - tải gói, áp gói bằng CLI và nhập kết quả, xem nhật ký.
    /// Chỉ chạy khi có FAST_PORTAL_SQL (xem SqlServerFactAttribute)
    /// </summary>
    [Collection("SqlServerPortal")]
    public sealed class SqlServerPortalSmokeTests
    {
        private const string Password = "MatKhau-Dai-12345";

        private sealed class RealFactory : WebApplicationFactory<AccountLoginService>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
        }

        private static async Task<string> TokenAsync(HttpClient http, string url)
        {
            string html = await (await http.GetAsync(url)).Content.ReadAsStringAsync();
            Match token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(token.Success, "không có antiforgery token ở " + url);
            return token.Groups[1].Value;
        }

        private static async Task<string> TextAsync(HttpClient http, string url) => WebUtility.HtmlDecode(await (await http.GetAsync(url)).Content.ReadAsStringAsync());

        private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string pageUrl, string postUrl, Dictionary<string, string> fields)
        {
            fields["__RequestVerificationToken"] = await TokenAsync(http, pageUrl);
            return await http.PostAsync(postUrl, new FormUrlEncodedContent(fields));
        }

        [SqlServerFact]
        public async Task Whole_portal_flow_works_on_a_real_database()
        {
            string connection = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)!;
            string sfx = Guid.NewGuid().ToString("N")[..6];
            string admin = "smoke_" + sfx, code = "SMK" + sfx.ToUpperInvariant(), customer = "khach_" + sfx;

            // Dữ liệu nền: dự án + administrator trung tâm (băm mật khẩu thật, như Cli create-portal-account)
            SqlControlPlaneStore store = new(new SqlDatabase(connection, 30));
            int project = (await store.CreateProjectAsync(code, "Dự án thử " + sfx, "https://smoke.example"))!.Value;
            await store.CreateAccountAsync(admin, "Quản trị thử", "staff", PortalPasswordHasher.Hash(Password, iterations: 20_000), new NewMembership(null, "administrator", null), null);

            string? previous = Environment.GetEnvironmentVariable("ControlPlane__ConnectionString");
            Environment.SetEnvironmentVariable("ControlPlane__ConnectionString", connection);
            try
            {
                using RealFactory factory = new();
                using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

                // Đăng nhập
                HttpResponseMessage login = await PostAsync(http, "/Admin/Login", "/Admin/Login", new() { ["Login"] = admin, ["Password"] = Password });
                Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
                HttpClient anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                // Sai mật khẩu: hiện lại trang đăng nhập, không có phiên
                Assert.Equal(HttpStatusCode.OK, (await PostAsync(anonymous, "/Admin/Login", "/Admin/Login", new() { ["Login"] = admin, ["Password"] = "sai-mat-khau-nhieu" })).StatusCode);
                Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin")).StatusCode);

                // Dashboard, dự án, danh sách
                string home = await TextAsync(http, "/Admin");
                Assert.Contains("Nhân viên hoạt động", home);
                Assert.Contains(code, home);
                Assert.Contains(code, await TextAsync(http, "/Admin/Projects"));
                Assert.Contains(admin, await TextAsync(http, "/Admin/Accounts?search=" + admin));

                // Tạo khách hàng, xem chi tiết, khóa
                HttpResponseMessage created = await PostAsync(http, "/Admin/Accounts/Create", "/Admin/Accounts/Create", new()
                {
                    ["Input.Login"] = customer, ["Input.DisplayName"] = "Khách thử", ["Input.Kind"] = "customer", ["Input.RoleCode"] = "partner",
                    ["Input.ProjectId"] = project.ToString(), ["Input.ProjectUser"] = "API_" + sfx, ["Input.Password"] = Password
                });
                Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
                string detailUrl = created.Headers.Location!.OriginalString;
                Assert.Contains(customer, await TextAsync(http, detailUrl));
                PortalAccount stored = (await store.FindAccountByLoginAsync(customer))!;
                Assert.True((await PostAsync(http, detailUrl, detailUrl + "?handler=SetActive", new() { ["active"] = "false" })).StatusCode == HttpStatusCode.Redirect);
                Assert.False((await store.FindAccountAsync(stored.Id))!.Active);

                // Form: lưu, publish, gói
                string editUrl = $"/Admin/Forms/Edit?project={project}&form=getCustomer";
                string template = FormManagementService.NewFormTemplate;
                Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(http, editUrl, editUrl + "&handler=Save", new() { ["content"] = template, ["baseRevision"] = "" })).StatusCode);
                Assert.Equal("validated", (await store.GetRevisionsAsync(project, "getCustomer")).Single().Status);
                Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(http, editUrl, editUrl + "&handler=Publish", new() { ["revision"] = "1" })).StatusCode);
                Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(http, $"/Admin/Packages?project={project}", $"/Admin/Packages?handler=Create&project={project}", new())).StatusCode);

                int packageId = (await store.ListPackagesAsync(project)).Single().Id;
                HttpResponseMessage download = await http.GetAsync($"/Admin/Packages?handler=Download&project={project}&id={packageId}");
                Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
                byte[] zip = await download.Content.ReadAsByteArrayAsync();
                PackageContents contents = FormPackage.Read(zip);
                Assert.Equal(contents.ManifestSha256, (await store.ListPackagesAsync(project)).Single().ManifestSha256);

                // Tùy chọn: xuất gói ra thư mục để chạy thử Cli apply-package bằng file thật
                if (Environment.GetEnvironmentVariable("FAST_PORTAL_DUMP_PACKAGE") is { Length: > 0 } dump)
                {
                    File.WriteAllBytes(Path.Combine(dump, "package.zip"), zip);
                    File.WriteAllText(Path.Combine(dump, "package.sha256"), contents.ManifestSha256);
                }

                // Áp gói bằng CLI (logic dùng chung) rồi nhập kết quả vào màn hình Đồng bộ
                string target = Path.Combine(Path.GetTempPath(), "smoke-" + sfx);
                Directory.CreateDirectory(target);
                try
                {
                    string json = FormPackageApplier.ResultToJson(FormPackageApplier.Run(zip, contents.ManifestSha256, target, apply: true, "PROD", "nguyen", TimeProvider.System));
                    Assert.True(File.Exists(Path.Combine(target, "getCustomer.xml")));

                    string token = await TokenAsync(http, "/Admin/Sync");
                    using MultipartFormDataContent body = new()
                    {
                        { new StringContent(token), "__RequestVerificationToken" },
                        { new StringContent(project.ToString()), "project" },
                        { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(json)), "result", "apply-result.json" }
                    };
                    Assert.Equal(HttpStatusCode.Redirect, (await http.PostAsync("/Admin/Sync?handler=Import", body)).StatusCode);
                }
                finally
                {
                    Directory.Delete(target, recursive: true);
                }

                SyncLogPage log = await store.ListSyncLogAsync([project], null, "", "", 1, 10);
                Assert.Equal(["APPLY", "EXPORT"], log.Items.Select(e => e.Event));
                Assert.Contains("APPLY", await TextAsync(http, $"/Admin/Sync?projectId={project}"));

                // Nhật ký audit thấy toàn bộ thao tác vừa làm
                string audit = await TextAsync(http, "/Admin/Audit?projectId=" + project);
                foreach (string action in new[] { "account.create", "account.lock", "form.save", "form.publish", "package.create", "sync.import" })
                {
                    Assert.Contains(action, audit);
                }
                Assert.Contains(admin, await TextAsync(http, "/Admin/Audit?action=login&person=" + admin));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ControlPlane__ConnectionString", previous);
            }
        }
    }
}
