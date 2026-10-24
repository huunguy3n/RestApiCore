using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Business;
using DataAccess;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Models;
using Security;
using Web.Custom;

namespace Tests
{
    /// <summary>
    /// Phân quyền portal: vai trò → quyền chi tiết kiểm ở server, userinfo2.admin không thành Administrator,
    /// quyền form giữ ngữ nghĩa "không dòng = mọi form" (danh sách rỗng bị từ chối), tên form phải có trong catalog
    /// </summary>
    public sealed class PortalAdminTests : IAsyncLifetime
    {
        private const string SecretKey = "khoa-ky-jwt-chi-dung-cho-test-0123456789";
        private static readonly JwtTokenService Jwt = new(SecretKey, "FastRestApi");

        private readonly FakeRepository _repo = new();
        private readonly FakeKeyStore _keys = new();
        private readonly FakeLogReader _logs = new();
        private IHost _host = null!;
        private HttpClient _http = null!;

        private const string Sale = "<import xmlns=\"urn:schemas-fast-com:fast-api\" type=\"Voucher\"><fields name=\"m\"><field name=\"a\" fastName=\"a\"/></fields><query><text>select 1</text></query></import>";

        public async Task InitializeAsync()
        {
            // 1 partner, 2 deployment, 3 administrator, 4 FBO admin không vai trò portal, 5 partner mục tiêu
            _keys.Add(new SignerKeys(1, "PARTNER", true, null, null, 11, "P"));
            _keys.Add(new SignerKeys(2, "STAFF", true, null, null, 12, "S"));
            _keys.Add(new SignerKeys(3, "ADMIN3", true, null, null, 13, "A"));
            _keys.Add(new SignerKeys(4, "ERPADMIN", true, null, null, 14, "E") { ErpAdmin = true });
            _keys.Add(new SignerKeys(5, "TARGET", true, null, null, 15, "T"));

            _repo.Roles[2] = (["deployment"], [PortalPermissions.FormsRead, PortalPermissions.FormsPermissionsManage, PortalPermissions.LogsRead]);
            _repo.Roles[3] = (["administrator"], [PortalPermissions.UsersRead, PortalPermissions.FormsPermissionsManage, PortalPermissions.LogsRead, PortalPermissions.LogsBodyRead, "khong.co.that"]);
            _repo.Users["TARGET"] = new UserFormAccess(5, "TARGET", false, []);
            _repo.Users["ERPADMIN"] = new UserFormAccess(4, "ERPADMIN", true, []);

            ImportForm Load(string name) => ColumnTypeResolver.Resolve(ImportFormLoader.Parse(name, XDocument.Parse(Sale)), _ => null);
            var catalog = ImportFormCatalog.Build([Load("setSaleInvoice"), Load("getCustomer")]);

            _host = await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(new AppConfig { pathDirectory = ApiDirectory(), clockSkewSeconds = 300, maxRequestBodyBytes = 64 * 1024, secretKey = SecretKey, issuerToken = "FastRestApi" });
                        services.AddSingleton<Models.Options.Options>(sp => new OptionsRepository(sp.GetRequiredService<AppConfig>()));
                        services.AddSingleton<ResponseManager>();
                        services.AddSingleton<NonceStore>();
                        services.AddSingleton<LoginLockout>();
                        services.AddMemoryCache();
                        services.AddSingleton<TimeProvider>(TimeProvider.System);
                        services.AddSingleton<ISignerKeyStore>(_keys);
                        services.AddSingleton<IRevokedTokenStore>(new NoRevoked());
                        services.AddSingleton<MessageAuthenticator>();
                        services.AddSingleton<IPortalAccessRepository>(_repo);
                        services.AddSingleton<PortalAuthorizationService>();
                        services.AddSingleton<IFormCatalogProvider>(new StaticCatalog(catalog));
                        services.AddSingleton<ISignerCacheInvalidator>(_repo);
                        services.AddSingleton<FormPermissionService>();
                        services.AddSingleton<UserAdminService>();
                        services.AddSingleton<IRequestLogReader>(_logs);
                        services.AddSingleton<RequestLogViewService>();
                        services.AddSingleton(NullLoggerFactory.Instance);
                        services.AddLogging();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<RequestIdMiddleware>();
                        app.Use(async (context, next) =>
                        {
                            string authorization = context.Request.Headers.Authorization.ToString();
                            if (authorization.StartsWith("Bearer ") && Jwt.ValidateJWTToken(authorization[7..]) is ClaimsPrincipal principal)
                            {
                                context.User = principal;
                            }
                            await next(context);
                        });
                        app.UseMiddleware<SignatureMiddleware>();
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapPortalAdmin());
                    }))
                .StartAsync();
            _http = _host.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            _http.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }

        private static string ApiDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WebApi", "App_Data", "Options", "Message.xml")))
            {
                dir = dir.Parent;
            }
            return Path.Combine(dir!.FullName, "WebApi");
        }

        private HttpRequestMessage Request(HttpMethod method, string path, SignerKeys? user, string scope = "portal", object? body = null)
        {
            var request = new HttpRequestMessage(method, "http://localhost" + path);
            if (user != null)
            {
                string token = Jwt.GenerateToken(user.Name, user.UId, user.UserId!.Value, user.NameErp, Guid.NewGuid().ToString(), DateTime.UtcNow.AddMinutes(10), scope);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            if (body != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            return request;
        }

        private async Task<(HttpStatusCode Status, JsonElement Json)> SendAsync(HttpRequestMessage request)
        {
            using HttpResponseMessage response = await _http.SendAsync(request);
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }

        private SignerKeys User(string name) => _keys.Get(name);

        [Fact]
        public async Task Partner_has_no_permissions_and_cannot_use_admin_handlers()
        {
            var (status, me) = await SendAsync(Request(HttpMethod.Get, "/api/_portal/me", User("PARTNER")));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(0, me.GetProperty("data").GetProperty("permissions").GetArrayLength());
            Assert.Equal("partner", me.GetProperty("data").GetProperty("roles")[0].GetString());

            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/users", User("PARTNER")))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/users/TARGET/forms", User("PARTNER")))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Put, "/api/_portal/users/TARGET/forms", User("PARTNER"), body: new { forms = new[] { "setSaleInvoice" } }))).Status);
            Assert.Empty(_repo.Replaced);
            Assert.Contains(_repo.Audit, a => a.Outcome == "DENIED" && a.Action == FormPermissionService.SetAction);
        }

        [Fact]
        public async Task Fbo_admin_does_not_become_portal_administrator()
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/users", User("ERPADMIN")))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Put, "/api/_portal/users/TARGET/forms", User("ERPADMIN"), body: new { forms = new[] { "setSaleInvoice" } }))).Status);
        }

        [Fact]
        public async Task Missing_tables_or_roles_deny()
        {
            _repo.Roles.Clear();
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/users", User("ADMIN3")))).Status);
        }

        [Fact]
        public async Task Staff_manages_form_permissions_but_not_users()
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/users", User("STAFF")))).Status);

            var (status, json) = await SendAsync(Request(HttpMethod.Put, "/api/_portal/users/TARGET/forms", User("STAFF"), body: new { forms = new[] { "SETSALEINVOICE", "setSaleInvoice" } }));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("setSaleInvoice", Assert.Single(_repo.Replaced).Forms.Single());
            Assert.Equal(1, json.GetProperty("data").GetProperty("forms").GetArrayLength());
            Assert.Equal(1, _repo.Invalidations);
            Assert.Contains(_repo.Audit, a => a.Outcome == "SUCCESS" && a.ActorName == "STAFF" && a.Target == "TARGET");
        }

        [Fact]
        public async Task Administrator_lists_users_without_secrets_and_unknown_permission_is_ignored()
        {
            var (status, json) = await SendAsync(Request(HttpMethod.Get, "/api/_portal/users?search=" + Uri.EscapeDataString("TA%[_]"), User("ADMIN3")));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("TA", _repo.LastSearch);
            string text = json.GetRawText();
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);

            var (_, me) = await SendAsync(Request(HttpMethod.Get, "/api/_portal/me", User("ADMIN3")));
            Assert.DoesNotContain("khong.co.that", me.GetRawText());
        }

        [Fact]
        public async Task Empty_or_unknown_form_lists_are_rejected_and_never_written()
        {
            foreach (object body in new object[] { new { forms = Array.Empty<string>() }, new { }, new { forms = new[] { "setSaleInvoice", "getSecret" } }, new { forms = new[] { "x'; DROP TABLE restApiUser;--" } } })
            {
                var (status, json) = await SendAsync(Request(HttpMethod.Put, "/api/_portal/users/TARGET/forms", User("STAFF"), body: body));
                Assert.Equal(HttpStatusCode.BadRequest, status);
                Assert.Contains(json.GetProperty("code").GetString(), new[] { "EMPTY_ALLOW_LIST", "INVALID_FORM" });
            }
            Assert.Empty(_repo.Replaced);
        }

        [Fact]
        public async Task Api_scope_token_and_anonymous_cannot_call_portal_admin()
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/me", User("ADMIN3"), scope: "api"))).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/me", null))).Status);
        }

        [Fact]
        public async Task Form_permission_view_reports_broad_access()
        {
            var (_, unrestricted) = await SendAsync(Request(HttpMethod.Get, "/api/_portal/users/TARGET/forms", User("STAFF")));
            Assert.Equal("unrestricted", unrestricted.GetProperty("data").GetProperty("mode").GetString());
            Assert.True(unrestricted.GetProperty("data").GetProperty("broadAccess").GetBoolean());

            var (_, erp) = await SendAsync(Request(HttpMethod.Get, "/api/_portal/users/ERPADMIN/forms", User("STAFF")));
            Assert.Equal("erpAdmin", erp.GetProperty("data").GetProperty("mode").GetString());

            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/users/NOBODY/forms", User("STAFF")))).Status);
        }

        [Fact]
        public async Task Logs_need_logs_read_and_bodies_need_separate_permission()
        {
            string id = FakeLogReader.Id.ToString();
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs", User("PARTNER")))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs/" + id, User("PARTNER")))).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs?user=TA%25", User("STAFF")))).Status);
            Assert.Equal("TA", _logs.LastFilter!.UserName);

            // Staff có logs.read: thấy metadata, không thấy body, không ghi audit đọc body
            var (_, staff) = await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs/" + id, User("STAFF")));
            Assert.False(staff.GetProperty("data").GetProperty("bodiesVisible").GetBoolean());
            Assert.DoesNotContain("SECRET-VALUE", staff.GetRawText());
            Assert.DoesNotContain(_repo.Audit, a => a.Action == "logs.body.read");

            // Administrator có logs.body.read: body hiện nhưng khóa bí mật bị che, và có audit
            var (_, admin) = await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs/" + id, User("ADMIN3")));
            string body = admin.GetProperty("data").GetProperty("requestBody").GetString()!;
            Assert.Contains("HD001", body);
            Assert.DoesNotContain("SECRET-VALUE", admin.GetRawText());
            Assert.Contains(_repo.Audit, a => a.Action == "logs.body.read" && a.ActorName == "ADMIN3" && a.Target == id);
        }

        [Fact]
        public async Task Log_filters_reject_bad_ranges_and_ids()
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs?from=2026-01-01&to=2026-06-01", User("STAFF")))).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs?reqId=khong-phai-guid", User("STAFF")))).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs/khong-phai-guid", User("STAFF")))).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(Request(HttpMethod.Get, "/api/_portal/logs/" + Guid.NewGuid(), User("STAFF")))).Status);
        }

        private sealed class FakeLogReader : IRequestLogReader
        {
            public static readonly Guid Id = Guid.NewGuid();
            public LogFilter? LastFilter { get; private set; }

            private static LogRequestSummary Summary => new(Id, DateTime.Now, "TARGET", "10.0.0.1", "POST", "/api/sale-invoice", "setSaleInvoice", 200, "SUCCESS", 12, 80, 40, "VALID");

            public Task<LogPage> ListAsync(LogFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
            {
                LastFilter = filter;
                return Task.FromResult(new LogPage(1, [Summary]));
            }

            public Task<LogRequestDetail?> GetAsync(Guid reqId, int maxBodyBytes, CancellationToken cancellationToken = default) =>
                Task.FromResult<LogRequestDetail?>(reqId == Id
                    ? new LogRequestDetail(Summary, "unitCode=A", Encoding.UTF8.GetBytes("{\"yourId\":\"HD001\",\"detail\":{\"password\":\"SECRET-VALUE\"}}"), Encoding.UTF8.GetBytes("{\"accessToken\":\"SECRET-VALUE\"}"))
                    : null);
        }

        private sealed class StaticCatalog(ImportFormCatalog catalog) : IFormCatalogProvider
        {
            public ImportFormCatalog Catalog => catalog;
        }

        private sealed class NoRevoked : IRevokedTokenStore
        {
            public Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default) => Task.FromResult(false);
            public Task RevokeAsync(string jti, int uId, DateTime expiresAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class FakeKeyStore : ISignerKeyStore
        {
            private readonly Dictionary<string, SignerKeys> _users = new(StringComparer.OrdinalIgnoreCase);
            public void Add(SignerKeys keys) => _users[keys.Name] = keys;
            public SignerKeys Get(string name) => _users[name];
            public Task<SignerKeys?> GetAsync(string userName, CancellationToken cancellationToken = default) => Task.FromResult(_users.GetValueOrDefault(userName));
        }

        private sealed class FakeRepository : IPortalAccessRepository, ISignerCacheInvalidator
        {
            public Dictionary<int, (string[] Roles, string[] Permissions)> Roles { get; } = [];
            public Dictionary<string, UserFormAccess> Users { get; } = [];
            public List<(int UId, string[] Forms)> Replaced { get; } = [];
            public List<AuditEvent> Audit { get; } = [];
            public string? LastSearch { get; private set; }
            public int Invalidations { get; private set; }

            public Task<(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions)> GetRolesAsync(int uId, CancellationToken cancellationToken = default) =>
                Task.FromResult(Roles.TryGetValue(uId, out var r) ? ((IReadOnlyList<string>)r.Roles, (IReadOnlyList<string>)r.Permissions) : ([], []));

            public Task<PortalUserPage> ListUsersAsync(string search, int page, int pageSize, CancellationToken cancellationToken = default)
            {
                LastSearch = search;
                return Task.FromResult(new PortalUserPage(1, [new PortalUserSummary(5, "TARGET", "T", true, false, [])]));
            }

            public Task<UserFormAccess?> GetUserFormsAsync(string userName, CancellationToken cancellationToken = default) => Task.FromResult(Users.GetValueOrDefault(userName));

            public Task ReplaceUserFormsAsync(int uId, IReadOnlyCollection<string> forms, CancellationToken cancellationToken = default)
            {
                Replaced.Add((uId, forms.ToArray()));
                return Task.CompletedTask;
            }

            public Task WriteAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            {
                Audit.Add(auditEvent);
                return Task.CompletedTask;
            }

            public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
            {
                Invalidations++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
