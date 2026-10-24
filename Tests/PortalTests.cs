using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
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
using Models;
using Security;
using Tests.Sample;
using Web.Custom;

namespace Tests
{
    /// <summary>
    /// Portal: đăng nhập RSA-OAEP (client mã hóa bằng JWK lấy từ /_meta/server như WebCrypto), JWT scope=portal chỉ đọc _meta
    /// </summary>
    public sealed class PortalTests : IAsyncLifetime
    {
        private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private const string SecretKey = "khoa-ky-jwt-chi-dung-cho-test-0123456789";
        private const string PasswordKey = "0123456789abcdef0123456789abcdef";
        private static readonly byte[] PrivateKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private static readonly JwtTokenService Jwt = new(SecretKey, "FastRestApi");

        private readonly PortalKeyProvider _portalKey = PortalKeyProvider.Ephemeral();
        private readonly FakeKeyStore _keys = new();
        private IHost _host = null!;
        private HttpClient _http = null!;

        private const string SaleInvoice = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="Voucher" unitRequired="true" description="Hóa đơn bán hàng">
              <fields name="master" key="yourId">
                <field name="yourId" fastName="id_doitac" maxLength="36" allowNull="false" example="HD001"/>
                <field name="voucherDate" fastName="ngay_ct" type="datetime" format="dd/MM/yyyy" allowNull="false"/>
                <field name="customerId" fastName="ma_kh" check="ma_kh = ''" errorCode="CUSTOMER_NOT_FOUND"/>
                <field name="secret" fastName="noi_bo" hidden="true"/>
              </fields>
              <fields name="detail">
                <field name="quantity" fastName="so_luong" type="numeric" example="2"/>
              </fields>
              <query><text>select 1</text></query>
            </import>
            """;

        public async Task InitializeAsync()
        {
            byte[] requestKey = MessageSignature.DeriveRequestKey(PrivateKey), responseKey = MessageSignature.DeriveResponseKey(PrivateKey);
            _keys.Add(new SignerKeys(7, "DOITAC", true, requestKey, responseKey, 1, "ADMIN") { AllowForms = new HashSet<string>(["setSaleInvoice"], StringComparer.OrdinalIgnoreCase) });
            _keys.Add(new SignerKeys(8, "CHUACOKHOA", true, null, null, 2, "USER2"));
            _keys.Add(new SignerKeys(9, "NGUNG", false, null, null, null, ""));

            ImportForm Load(string name, string xml) => ColumnTypeResolver.Resolve(ImportFormLoader.Parse(name, XDocument.Parse(xml)), _ => null);
            var catalog = ImportFormCatalog.Build([Load("setSaleInvoice", SaleInvoice), Load("getSecret", "<import xmlns=\"urn:schemas-fast-com:fast-api\" type=\"List\"><fields name=\"f\"><field name=\"a\" fastName=\"a\"/></fields><query><text>select 1</text></query></import>")]);

            _host = await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        var appConfig = new AppConfig
                        {
                            pathDirectory = ApiDirectory(), clockSkewSeconds = 300, maxRequestBodyBytes = 64 * 1024, secretKey = SecretKey, passwordKey = PasswordKey,
                            issuerToken = "FastRestApi", tokenMinutes = 60, portalTokenMinutes = 30, loginMaxFailures = 3, loginLockMinutes = 15
                        };
                        services.AddSingleton(appConfig);
                        services.AddMemoryCache();
                        services.AddSingleton<Models.Options.Options>(new OptionsRepository(appConfig));
                        services.AddSingleton<ResponseManager>();
                        services.AddSingleton<NonceStore>();
                        services.AddSingleton<LoginLockout>();
                        services.AddSingleton<ISignerKeyStore>(_keys);
                        services.AddSingleton<IRevokedTokenStore>(new NoRevoked());
                        services.AddSingleton<IIpAccessManager, AllowAllIp>();
                        services.AddSingleton<IPortalCredentialStore>(new FakeCredentials());
                        services.AddSingleton(_portalKey);
                        services.AddSingleton(new FormRuntime(catalog, null));
                        services.AddSingleton<TimeProvider>(TimeProvider.System);
                        services.AddSingleton(Jwt);
                        services.AddSingleton<MessageAuthenticator>();
                        services.AddSingleton<AccessTokenIssuer>();
                        services.AddSingleton<PortalLoginService>();
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
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/api/_meta/server", () => Results.Json(new { serverTime = Now, portalKey = _portalKey.PublicJwk() }));
                            endpoints.MapPost("/api/_meta/ping", () => "{\"code\":\"SUCCESS\"}");
                            endpoints.MapPortal();
                            endpoints.MapPost("/api/sale-invoice", () => "{\"code\":\"SUCCESS\"}");
                        });
                    }))
                .StartAsync();
            _http = _host.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            _http.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            _portalKey.Dispose();
        }

        /// <summary>Như trình duyệt: lấy JWK từ /_meta/server, mã hóa { password, timestamp, nonce } bằng RSA-OAEP SHA-256</summary>
        private async Task<(string Kid, string Credential)> EncryptAsync(string password, long timestamp, string nonce)
        {
            var jwk = JsonDocument.Parse(await _http.GetStringAsync("http://localhost/api/_meta/server")).RootElement.GetProperty("portalKey");
            using RSA rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = FromBase64Url(jwk.GetProperty("n").GetString()!), Exponent = FromBase64Url(jwk.GetProperty("e").GetString()!) });
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new { password, timestamp, nonce });
            return (jwk.GetProperty("kid").GetString()!, Convert.ToBase64String(rsa.Encrypt(plain, RSAEncryptionPadding.OaepSHA256)));
        }

        private async Task<(HttpResponseMessage Response, JsonElement Json)> LoginAsync(string user, string password, long? timestamp = null, string? nonce = null, string? kid = null, string? credential = null)
        {
            var (realKid, realCredential) = await EncryptAsync(password, timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), nonce ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
            var body = JsonSerializer.Serialize(new { userName = user, kid = kid ?? realKid, credential = credential ?? realCredential });
            HttpResponseMessage response = await _http.PostAsync("http://localhost/api/auth/portal", new StringContent(body, Encoding.UTF8, "application/json"));
            return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }

        private static string Code(JsonElement json) => json.GetProperty("code").GetString()!;

        private async Task<string> PortalTokenAsync(string user = "DOITAC") => (await LoginAsync(user, "mat-khau-portal")).Json.GetProperty("accessToken").GetString()!;

        private async Task<(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Response)> GetAsync(string path, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response = await _http.SendAsync(request);
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone(), response);
        }

        [Fact]
        public async Task DangNhapDung_TraJwtScopePortal_KhongKy()
        {
            var (response, json) = await LoginAsync("DOITAC", "mat-khau-portal");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("portal", json.GetProperty("scope").GetString());
            Assert.Equal(1800, json.GetProperty("expiresIn").GetInt32());
            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(json.GetProperty("accessToken").GetString());
            Assert.Equal("portal", jwt.Claims.First(c => c.Type == "scope").Value);
            Assert.Equal("7", jwt.Claims.First(c => c.Type == "u_id").Value);
            Assert.False(response.Headers.Contains("X-Fast-Signature"));
        }

        [Fact]
        public async Task TaiKhoanChuaCapPrivateKey_VanDangNhapPortalDuoc()
        {
            var (response, _) = await LoginAsync("CHUACOKHOA", "mat-khau-portal");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Theory]
        [InlineData("DOITAC", "sai-mat-khau", "INVALID_CREDENTIAL", HttpStatusCode.Unauthorized)]
        [InlineData("KHONGCO", "mat-khau-portal", "UNAUTHORIZED", HttpStatusCode.Unauthorized)]
        [InlineData("NGUNG", "mat-khau-portal", "UNAUTHORIZED", HttpStatusCode.Unauthorized)]
        public async Task DangNhapSai(string user, string password, string code, HttpStatusCode status)
        {
            var (response, json) = await LoginAsync(user, password);

            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, Code(json));
            Assert.False(json.TryGetProperty("accessToken", out _));
        }

        [Fact]
        public async Task SaiNhieuLan_KhoaDangNhap_KeCaMatKhauDung()
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal("INVALID_CREDENTIAL", Code((await LoginAsync("DOITAC", "sai")).Json));
            }
            Assert.Equal("ACCOUNT_LOCKED", Code((await LoginAsync("DOITAC", "mat-khau-portal")).Json));
        }

        [Fact]
        public async Task PhatLaiGoiDangNhap_NonceReused()
        {
            const string nonce = "nonce-portal-000001";
            Assert.Equal("SUCCESS", Code((await LoginAsync("DOITAC", "mat-khau-portal", nonce: nonce)).Json));
            Assert.Equal("NONCE_REUSED", Code((await LoginAsync("DOITAC", "mat-khau-portal", nonce: nonce)).Json));
        }

        [Theory]
        [InlineData(-330)]
        [InlineData(330)]
        public async Task GoiDangNhapQuaHan_TimestampExpired(long offset)
        {
            Assert.Equal("TIMESTAMP_EXPIRED", Code((await LoginAsync("DOITAC", "mat-khau-portal", timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offset)).Json));
        }

        [Fact]
        public async Task SaiKid_HoacBanMaHong()
        {
            Assert.Equal("INVALID_KID", Code((await LoginAsync("DOITAC", "mat-khau-portal", kid: "kid-cu")).Json));
            Assert.Equal("INVALID_CREDENTIAL", Code((await LoginAsync("DOITAC", "mat-khau-portal", credential: Convert.ToBase64String(new byte[256]))).Json));
            Assert.Equal("INVALID_CREDENTIAL", Code((await LoginAsync("DOITAC", "mat-khau-portal", credential: "khong-phai-base64")).Json));
        }

        [Fact]
        public async Task GuiMatKhauRo_KhongDangNhapDuoc()
        {
            var body = JsonSerializer.Serialize(new { userName = "DOITAC", kid = _portalKey.Kid, credential = Convert.ToBase64String(Encoding.UTF8.GetBytes("mat-khau-portal")) });
            HttpResponseMessage response = await _http.PostAsync("http://localhost/api/auth/portal", new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task TokenPortal_DocDanhSachVaDinhNghia()
        {
            string token = await PortalTokenAsync();

            var (status, list, response) = await GetAsync("/api/_meta/resources", token);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(["setSaleInvoice"], list.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("form").GetString()));
            Assert.False(response.Headers.Contains("X-Fast-Signature"));

            var (_, detail, _) = await GetAsync("/api/_meta/resources/setsaleinvoice", token);
            JsonElement data = detail.GetProperty("data");
            Assert.Equal("/api/sale-invoice", data.GetProperty("summary").GetProperty("route").GetString());
            Assert.True(data.GetProperty("summary").GetProperty("unitRequired").GetBoolean());
            JsonElement master = data.GetProperty("blocks")[0];
            Assert.Equal(["yourId", "voucherDate", "customerId"], master.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
            Assert.Equal("dd/MM/yyyy hoặc ISO 8601", master.GetProperty("fields")[1].GetProperty("format").GetString());
            Assert.Equal("CUSTOMER_NOT_FOUND", master.GetProperty("fields")[2].GetProperty("errorCodes")[0].GetString());
            JsonElement sample = data.GetProperty("sample");
            Assert.Equal("HD001", sample.GetProperty("master").GetProperty("yourId").GetString());
            Assert.Equal("21/10/2026", sample.GetProperty("master").GetProperty("voucherDate").GetString());
            Assert.Equal(2m, sample.GetProperty("detail")[0].GetProperty("quantity").GetDecimal());
            Assert.False(sample.GetProperty("master").TryGetProperty("secret", out _), "field hidden không hiện trong mẫu");
        }

        [Theory]
        [InlineData("/api/_meta/resources/getSecret", "FORBIDDEN", HttpStatusCode.Forbidden)]
        [InlineData("/api/_meta/resources/khongCo", "NOT_FOUND", HttpStatusCode.NotFound)]
        public async Task TokenPortal_DinhNghiaKhongDuocPhep(string path, string code, HttpStatusCode expected)
        {
            var (status, json, _) = await GetAsync(path, await PortalTokenAsync());

            Assert.Equal(expected, status);
            Assert.Equal(code, Code(json));
        }

        [Fact]
        public async Task TokenPortal_DocBangMaLoi()
        {
            var (_, json, _) = await GetAsync("/api/_meta/errors", await PortalTokenAsync());

            Assert.Contains(json.GetProperty("data").EnumerateArray(), e => e.GetProperty("code").GetString() == "INVALID_SIGNATURE" && e.GetProperty("httpStatus").GetInt32() == 401);
        }

        [Theory]
        [InlineData("POST", "/api/sale-invoice")]
        [InlineData("POST", "/api/_meta/ping")]
        [InlineData("POST", "/api/_meta/resources")]
        public async Task TokenPortal_KhongGoiDuocRouteNghiepVu(string method, string path)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), "http://localhost" + path) { Content = new StringContent("{}") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await PortalTokenAsync());

            HttpResponseMessage response = await _http.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("PORTAL_TOKEN_NOT_ALLOWED", Code(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement));
        }

        [Fact]
        public async Task TokenApi_DocMetaCoKy()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/_meta/resources");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt.GenerateToken("DOITAC", 7, 1, "ADMIN", Guid.NewGuid().ToString(), DateTime.UtcNow.AddHours(1)));
            var signer = new FastApiSigner(PrivateKey);
            string nonce = signer.Sign(request, [], Now);
            request.Content = null;

            HttpResponseMessage response = await _http.SendAsync(request);
            byte[] body = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(signer.VerifyResponse(response, body, nonce));
        }

        [Fact]
        public void Kid_LaThumbprintRfc7638()
        {
            // Vector RFC 7638 mục 3.1
            var parameters = new RSAParameters
            {
                Modulus = FromBase64Url("0vx7agoebGcQSuuPiLJXZptN9nndrQmbXEps2aiAFbWhM78LhWx4cbbfAAtVT86zwu1RK7aPFFxuhDR1L6tSoc_BJECPebWKRXjBZCiFV4n3oknjhMstn64tZ_2W-5JsGY4Hc5n9yBXArwl93lqt7_RN5w6Cf0h4QyQ5v-65YGjQR0_FDW2QvzqY368QQMicAtaSqzs8KJZgnYb9c7d0zgdAZHzu6qMQvRL5hajrn1n91CbOpbISD08qNLyrdkt-bFTWhAI4vMQFh6WeZu0fM4lFd2NcRwr3XPksINHaQ-G_xBniIqbw0Ls1jF44-csFCur-kEgU8awapJzKnqDKgw"),
                Exponent = FromBase64Url("AQAB")
            };
            Assert.Equal("NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs", PortalKeyProvider.Thumbprint(parameters));
        }

        private static byte[] FromBase64Url(string value)
        {
            string base64 = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
        }

        private static string ApiDirectory()
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WebApi", "App_Data", "Options", "Message.xml")))
            {
                dir = dir.Parent;
            }
            return Path.Combine(dir!.FullName, "WebApi");
        }

        private sealed class FakeCredentials : IPortalCredentialStore
        {
            public Task<string?> GetEncryptedPasswordAsync(string userName, CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(userName is "DOITAC" or "CHUACOKHOA" ? EncryptionHelper.EncryptString("mat-khau-portal", PasswordKey) : null);
        }

        private sealed class FakeKeyStore : ISignerKeyStore
        {
            private readonly Dictionary<string, SignerKeys> _keys = new(StringComparer.OrdinalIgnoreCase);
            public void Add(SignerKeys keys) => _keys[keys.Name] = keys;
            public Task<SignerKeys?> GetAsync(string userName, CancellationToken cancellationToken = default) => Task.FromResult(_keys.GetValueOrDefault(userName));
        }

        private sealed class NoRevoked : IRevokedTokenStore
        {
            public Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default) => Task.FromResult(false);
            public Task RevokeAsync(string jti, int uId, DateTime expiresAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class AllowAllIp(ResponseManager responseManager) : IIpAccessManager
        {
            public Task<Models.Interface.IResponse> CheckAsync(string clientIp, string userName, CancellationToken cancellationToken = default) =>
                Task.FromResult(responseManager.GetResponse("SUCCESS"));
        }
    }
}
