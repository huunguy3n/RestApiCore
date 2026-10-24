using System.Net;
using System.Security.Claims;
using System.Text;
using Business;
using DataAccess;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Models;
using Tests.Sample;
using Web.Custom;

namespace Tests
{
    /// <summary>
    /// SignatureMiddleware qua TestServer: client mẫu (bản cài độc lập) gọi server thật của middleware.
    /// Khóa ký và giờ là giả, không cần DB
    /// </summary>
    public sealed class SignatureMiddlewareTests : IAsyncLifetime
    {
        // Giờ thật (làm tròn giây): JWT do endpoint cấp được kiểm hạn theo đồng hồ thật
        private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private const string SecretKey = "khoa-ky-jwt-chi-dung-cho-test-0123456789";
        private const string Path = "http://localhost/api/_meta/ping?unitCode=CTY&dryRun=false";
        private static readonly byte[] PrivateKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"master\":{\"YourID\":\"HD001\"}}");

        private readonly FakeTime _time = new(Now);
        private readonly FakeKeyStore _keys = new();
        private readonly FakeRevokedStore _revoked = new();
        private readonly FastApiSigner _client = new(PrivateKey);
        private IHost _host = null!;
        private HttpClient _http = null!;

        public async Task InitializeAsync()
        {
            byte[] requestKey = Security.MessageSignature.DeriveRequestKey(PrivateKey), responseKey = Security.MessageSignature.DeriveResponseKey(PrivateKey);
            _keys.Add(new SignerKeys(7, "DOITAC", true, requestKey, responseKey, 1, "ADMIN"));
            _keys.Add(new SignerKeys(8, "CHUACOKHOA", true, null, null, 2, "USER2"));
            _keys.Add(new SignerKeys(9, "BICHANIP", true, requestKey, responseKey, 3, "USER3"));
            _keys.Add(new SignerKeys(10, "NGUNG", false, requestKey, responseKey, null, ""));

            _host = await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        var appConfig = new AppConfig
                        {
                            pathDirectory = ApiDirectory(), clockSkewSeconds = 300, maxRequestBodyBytes = 1024,
                            secretKey = SecretKey, issuerToken = "FastRestApi", tokenMinutes = 60, loginMaxFailures = 3, loginLockMinutes = 15
                        };
                        services.AddSingleton(appConfig);
                        services.AddMemoryCache();
                        services.AddSingleton<Models.Options.Options>(new OptionsRepository(appConfig));
                        services.AddSingleton<ResponseManager>();
                        services.AddSingleton<NonceStore>();
                        services.AddSingleton<ISignerKeyStore>(_keys);
                        services.AddSingleton<IRevokedTokenStore>(_revoked);
                        services.AddSingleton<IIpAccessManager, FakeIpAccess>();
                        services.AddSingleton<LoginLockout>();
                        services.AddSingleton<TimeProvider>(_time);
                        services.AddSingleton(Jwt);
                        services.AddSingleton<MessageAuthenticator>();
                        services.AddSingleton<AccessTokenIssuer>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<RequestIdMiddleware>();
                        // Xác thực JWT như CustomJwtAuthenticationHandler (cùng JwtTokenService)
                        app.Use(async (context, next) =>
                        {
                            string authorization = context.Request.Headers.Authorization.ToString();
                            if (authorization.StartsWith("Bearer "))
                            {
                                ClaimsPrincipal? principal = Jwt.ValidateJWTToken(authorization[7..], context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW");
                                if (principal != null)
                                {
                                    context.User = principal;
                                }
                            }
                            await next(context);
                        });
                        app.UseMiddleware<SignatureMiddleware>();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/api/_meta/ping", async context =>
                            {
                                using var reader = new StreamReader(context.Request.Body);
                                await context.Response.WriteAsync("{\"code\":\"SUCCESS\",\"echo\":" + await reader.ReadToEndAsync() + "}");
                            });
                            endpoints.MapGet("/api/_meta/server", () => "server");
                            endpoints.MapAuth();
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
        }

        private static readonly Security.JwtTokenService Jwt = new(SecretKey, 60, "FastRestApi");

        /// <summary>Request có JWT v3 hợp lệ của user (u_id lấy từ kho khóa giả trừ khi chỉ định)</summary>
        private HttpRequestMessage NewRequest(string user = "DOITAC", string path = Path, int? uId = null, string? token = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            token ??= Jwt.GenerateToken(user, uId ?? _keys.UId(user), 1, user, Guid.NewGuid().ToString(), DateTime.UtcNow.AddHours(1));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        private HttpRequestMessage TokenRequest(string user, FastApiSigner? signer, out string nonce)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/auth/token");
            nonce = (signer ?? _client).Sign(request, Encoding.UTF8.GetBytes("{\"userName\":\"" + user + "\"}"), Now);
            return request;
        }

        private async Task<string> GetTokenAsync(string user = "DOITAC")
        {
            var (response, code, verified) = await SendAsync(TokenRequest(user, null, out string nonce), nonce);
            Assert.Equal("SUCCESS", code);
            Assert.True(verified);
            string body = await response.Content.ReadAsStringAsync();
            return System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("accessToken").GetString()!;
        }

        private async Task<(HttpResponseMessage Response, string Code, bool Verified)> SendAsync(HttpRequestMessage request, string nonce)
        {
            HttpResponseMessage response = await _http.SendAsync(request);
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            string text = Encoding.UTF8.GetString(body);
            string code = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("code").GetString()!;
            return (response, code, _client.VerifyResponse(response, body, nonce));
        }

        [Fact]
        public async Task KyDung_QuaVaResponseDuocKy()
        {
            var request = NewRequest();
            string nonce = _client.Sign(request, Body, Now, idempotencyKey: "key-1");

            var (response, code, verified) = await SendAsync(request, nonce);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("SUCCESS", code);
            Assert.True(verified, "client kiểm được chữ ký response");
            Assert.Contains("HD001", await response.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("/api/cpanel")]
        [InlineData("/api/document/get")]
        public async Task TienToControllerRongDaBo_PhaiKy(string path)
        {
            // CPanelController/DocumentController rỗng đã xoá: hai tiền tố này không còn được miễn chữ ký
            HttpResponseMessage response = await _http.PostAsync("http://localhost" + path, new ByteArrayContent(Body));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("UNAUTHORIZED", System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public async Task SuaMotByteBody_InvalidSignature()
        {
            var request = NewRequest();
            string nonce = _client.Sign(request, Body, Now);
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"master\":{\"YourID\":\"HD002\"}}"));

            var (response, code, verified) = await SendAsync(request, nonce);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("INVALID_SIGNATURE", code);
            Assert.True(verified, "lỗi sau khi biết khóa vẫn được ký");
        }

        [Fact]
        public async Task KhoaKhac_InvalidSignature()
        {
            var request = NewRequest();
            string nonce = new FastApiSigner(new byte[32]).Sign(request, Body, Now);

            var (_, code, verified) = await SendAsync(request, nonce);

            Assert.Equal("INVALID_SIGNATURE", code);
            Assert.True(verified, "server ký bằng khóa thật của user, không phải khóa client dùng");
        }

        [Theory]
        [InlineData(-301)]
        [InlineData(301)]
        public async Task TimestampQuaHan_TimestampExpired(long offset)
        {
            var request = NewRequest();
            string nonce = _client.Sign(request, Body, Now + offset);

            var (_, code, _) = await SendAsync(request, nonce);

            Assert.Equal("TIMESTAMP_EXPIRED", code);
        }

        [Fact]
        public async Task DungLaiNonce_NonceReused()
        {
            var first = NewRequest();
            string nonce = _client.Sign(first, Body, Now, nonce: Now + "-dunglai0001");
            Assert.Equal("SUCCESS", (await SendAsync(first, nonce)).Code);

            var replay = NewRequest();
            _client.Sign(replay, Body, Now, nonce: Now + "-dunglai0001");
            var (response, code, _) = await SendAsync(replay, nonce);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("NONCE_REUSED", code);
        }

        [Fact]
        public async Task ChuKySai_KhongDotNonce()
        {
            var forged = NewRequest();
            new FastApiSigner(new byte[32]).Sign(forged, Body, Now, nonce: Now + "-bigiamao01");
            Assert.Equal("INVALID_SIGNATURE", (await SendAsync(forged, Now + "-bigiamao01")).Code);

            var genuine = NewRequest();
            string nonce = _client.Sign(genuine, Body, Now, nonce: Now + "-bigiamao01");
            Assert.Equal("SUCCESS", (await SendAsync(genuine, nonce)).Code);
        }

        [Theory]
        [InlineData("ngan")]
        [InlineData("co dau cach trong nonce")]
        [InlineData("khong-co-thoi-gian-0001")]
        [InlineData("1792574400dinhkemlien0001")]
        public async Task NonceSaiDang_MissingSignature(string nonce)
        {
            var request = NewRequest();
            _client.Sign(request, Body, Now, nonce: nonce);

            var (_, code, _) = await SendAsync(request, nonce);

            Assert.Equal("MISSING_SIGNATURE", code);
        }

        [Fact]
        public async Task ThieuHeaderKy_MissingSignature()
        {
            var request = NewRequest();
            request.Content = new ByteArrayContent(Body);

            var (_, code, verified) = await SendAsync(request, "");

            Assert.Equal("MISSING_SIGNATURE", code);
            Assert.True(verified);
        }

        [Fact]
        public async Task BodyQuaLon_PayloadTooLarge()
        {
            var request = NewRequest();
            string nonce = _client.Sign(request, new byte[2048], Now);

            var (response, code, _) = await SendAsync(request, nonce);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("PAYLOAD_TOO_LARGE", code);
        }

        [Fact]
        public async Task ChuaXacThuc_UnauthorizedKhongKy()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Path);
            string nonce = _client.Sign(request, Body, Now);

            var (response, code, verified) = await SendAsync(request, nonce);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("UNAUTHORIZED", code);
            Assert.False(verified);
            Assert.False(response.Headers.Contains("X-Fast-Signature"));
        }

        [Fact]
        public async Task ChuaCapKhoa_KeyNotIssued()
        {
            var request = NewRequest("CHUACOKHOA");
            string nonce = _client.Sign(request, Body, Now);

            var (response, code, _) = await SendAsync(request, nonce);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("KEY_NOT_ISSUED", code);
            Assert.False(response.Headers.Contains("X-Fast-Signature"));
        }

        [Fact]
        public async Task MetaServer_KhongCanKy()
        {
            HttpResponseMessage response = await _http.GetAsync("/api/_meta/server");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("X-Fast-Signature"));
        }

        [Fact]
        public async Task ResponseCuPhatLaiChoRequestKhac_KhongKiemDuoc()
        {
            var first = NewRequest();
            string nonce = _client.Sign(first, Body, Now);
            HttpResponseMessage response = await _http.SendAsync(first);
            byte[] body = await response.Content.ReadAsByteArrayAsync();

            Assert.True(_client.VerifyResponse(response, body, nonce));
            Assert.False(_client.VerifyResponse(response, body, "nonce-cua-request-khac"));
        }

        private static string ApiDirectory()
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Api", "App_Data", "Options", "Message.xml")))
            {
                dir = dir.Parent;
            }
            return System.IO.Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Không tìm thấy Api/App_Data"), "Api");
        }

        [Fact]
        public async Task AuthToken_KyDung_TraJwtV3_DungDuocChoRequestSau()
        {
            var (response, code, verified) = await SendAsync(TokenRequest("doitac", null, out string nonce), nonce);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("SUCCESS", code);
            Assert.True(verified, "response auth/token được ký");

            var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(3600, json.GetProperty("expiresIn").GetInt32());
            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(json.GetProperty("accessToken").GetString());
            Assert.Equal("DOITAC", jwt.Subject);
            Assert.Equal("7", jwt.Claims.First(c => c.Type == "u_id").Value);
            Assert.Equal("1", jwt.Claims.First(c => c.Type == "user_id").Value);
            Assert.Equal("ADMIN", jwt.Claims.First(c => c.Type == "name_erp").Value);
            Assert.Contains(jwt.Claims, c => c.Type == "jti");
            Assert.Contains(Security.JwtTokenService.Audience, jwt.Audiences);

            var ping = NewRequest(token: json.GetProperty("accessToken").GetString());
            string pingNonce = _client.Sign(ping, Body, Now);
            Assert.Equal("SUCCESS", (await SendAsync(ping, pingNonce)).Code);
        }

        [Fact]
        public async Task AuthToken_IpKhongDuocPhep_AccessDenied()
        {
            var (response, code, _) = await SendAsync(TokenRequest("BICHANIP", null, out string nonce), nonce);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("ACCESS_DENIED", code);
        }

        [Theory]
        [InlineData("KHONGCO")]
        [InlineData("NGUNG")]
        [InlineData("")]
        public async Task AuthToken_UserKhongHopLe_UnauthorizedKhongKy(string user)
        {
            var (response, code, _) = await SendAsync(TokenRequest(user, null, out string nonce), nonce);

            Assert.Equal("UNAUTHORIZED", code);
            Assert.False(response.Headers.Contains("X-Fast-Signature"));
        }

        [Fact]
        public async Task AuthToken_SaiChuKyNhieuLan_KhoaDangNhap()
        {
            var forger = new FastApiSigner(new byte[32]);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal("INVALID_SIGNATURE", (await SendAsync(TokenRequest("DOITAC", forger, out string forgedNonce), forgedNonce)).Code);
            }

            var (response, code, verified) = await SendAsync(TokenRequest("DOITAC", null, out string nonce), nonce);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("ACCOUNT_LOCKED", code);
            Assert.True(verified);
        }

        [Fact]
        public async Task AuthRevoke_TokenBiThuHoi_RequestSauBiTuChoi()
        {
            string token = await GetTokenAsync();

            var revoke = NewRequest(path: "http://localhost/api/auth/revoke", token: token);
            string nonce = _client.Sign(revoke, [], Now);
            var (_, code, verified) = await SendAsync(revoke, nonce);
            Assert.Equal("SUCCESS", code);
            Assert.True(verified);

            var ping = NewRequest(token: token);
            string pingNonce = _client.Sign(ping, Body, Now);
            var (response, pingCode, _) = await SendAsync(ping, pingNonce);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("UNAUTHORIZED", pingCode);
        }

        [Fact]
        public async Task TokenKieuCu_KhongCoUId_KhongDungDuocChoV3()
        {
            string oldToken = Jwt.GenerateJWTToken("DOITAC", "ADMIN", false, "UNKNOW");
            var request = NewRequest(token: oldToken);
            string nonce = _client.Sign(request, Body, Now);

            Assert.Equal("UNAUTHORIZED", (await SendAsync(request, nonce)).Code);
        }

        [Fact]
        public async Task TokenUIdKhongKhopTaiKhoan_Unauthorized()
        {
            var request = NewRequest(uId: 99);
            string nonce = _client.Sign(request, Body, Now);

            Assert.Equal("UNAUTHORIZED", (await SendAsync(request, nonce)).Code);
        }

        private sealed class FakeRevokedStore : IRevokedTokenStore
        {
            private readonly HashSet<string> _revoked = new();
            public Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default) => Task.FromResult(_revoked.Contains(jti));
            public Task RevokeAsync(string jti, int uId, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
            {
                _revoked.Add(jti);
                return Task.CompletedTask;
            }
        }

        /// <summary>BICHANIP bị chặn IP, còn lại được phép</summary>
        private sealed class FakeIpAccess(ResponseManager responseManager) : IIpAccessManager
        {
            public Task<Models.Interface.IResponse> CheckAsync(string clientIp, string userName, CancellationToken cancellationToken = default) =>
                Task.FromResult(responseManager.GetResponse(userName == "BICHANIP" ? "ACCESS_DENIED" : "SUCCESS"));
        }

        private sealed class FakeTime(long unixSeconds) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        private sealed class FakeKeyStore : ISignerKeyStore
        {
            // Như cột restApiUser.name trên SQL: không phân biệt hoa thường
            private readonly Dictionary<string, SignerKeys> _keys = new(StringComparer.OrdinalIgnoreCase);
            public void Add(SignerKeys keys) => _keys[keys.Name] = keys;
            public int UId(string userName) => _keys.TryGetValue(userName, out var keys) ? keys.UId : 0;
            public Task<SignerKeys?> GetAsync(string userName, CancellationToken cancellationToken = default) =>
                Task.FromResult(_keys.GetValueOrDefault(userName));
        }
    }
}
