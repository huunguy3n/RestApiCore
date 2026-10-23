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
        private const long Now = 1792574400;
        private const string Path = "http://localhost/api/v3/_meta/ping?unitCode=CTY&dryRun=false";
        private static readonly byte[] PrivateKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"master\":{\"YourID\":\"HD001\"}}");

        private readonly FakeTime _time = new(Now);
        private readonly FakeKeyStore _keys = new();
        private readonly FastApiV3Signer _client = new(PrivateKey);
        private IHost _host = null!;
        private HttpClient _http = null!;

        public async Task InitializeAsync()
        {
            _keys.Add(new SignerKeys(7, "DOITAC", true, Security.MessageSignature.DeriveRequestKey(PrivateKey), Security.MessageSignature.DeriveResponseKey(PrivateKey)));
            _keys.Add(new SignerKeys(8, "CHUACOKHOA", true, null, null));

            _host = await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        var appConfig = new AppConfig { pathDirectory = ApiDirectory(), clockSkewSeconds = 300, maxRequestBodyBytes = 1024 };
                        services.AddSingleton(appConfig);
                        services.AddMemoryCache();
                        services.AddSingleton<Models.Options.Options>(new OptionsRepository(appConfig));
                        services.AddSingleton<ResponseManager>();
                        services.AddSingleton<NonceStore>();
                        services.AddSingleton<ISignerKeyStore>(_keys);
                        services.AddSingleton<TimeProvider>(_time);
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<RequestIdMiddleware>();
                        // Giả xác thực JWT: header X-Test-User thành claim sub
                        app.Use(async (context, next) =>
                        {
                            string user = context.Request.Headers["X-Test-User"].ToString();
                            if (user.Length > 0)
                            {
                                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "Test"));
                            }
                            await next(context);
                        });
                        app.UseMiddleware<SignatureMiddleware>();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/api/v3/_meta/ping", async context =>
                            {
                                using var reader = new StreamReader(context.Request.Body);
                                await context.Response.WriteAsync("{\"code\":\"SUCCESS\",\"echo\":" + await reader.ReadToEndAsync() + "}");
                            });
                            endpoints.MapGet("/api/v3/_meta/server", () => "server");
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

        private HttpRequestMessage NewRequest(string user = "DOITAC", string path = Path)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-Test-User", user);
            return request;
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

        [Fact]
        public async Task SuaMotByteBody_ChecksumMismatch()
        {
            var request = NewRequest();
            string nonce = _client.Sign(request, Body, Now);
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"master\":{\"YourID\":\"HD002\"}}"));

            var (response, code, verified) = await SendAsync(request, nonce);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("CHECKSUM_MISMATCH", code);
            Assert.True(verified, "lỗi sau khi biết khóa vẫn được ký");
        }

        [Fact]
        public async Task DoiQuery_InvalidSignature()
        {
            var request = NewRequest();
            string nonce = _client.Sign(request, Body, Now);
            request.RequestUri = new Uri("http://localhost/api/v3/_meta/ping?unitCode=CTY&dryRun=true");

            var (response, code, _) = await SendAsync(request, nonce);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("INVALID_SIGNATURE", code);
        }

        [Fact]
        public async Task DoiIdempotencyKey_InvalidSignature()
        {
            var request = NewRequest();
            string nonce = _client.Sign(request, Body, Now, idempotencyKey: "key-1");
            request.Headers.Remove("Idempotency-Key");
            request.Headers.Add("Idempotency-Key", "key-2");

            var (_, code, _) = await SendAsync(request, nonce);

            Assert.Equal("INVALID_SIGNATURE", code);
        }

        [Fact]
        public async Task KhoaKhac_InvalidSignature()
        {
            var request = NewRequest();
            string nonce = new FastApiV3Signer(new byte[32]).Sign(request, Body, Now);

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
            string nonce = _client.Sign(first, Body, Now, nonce: "nonce-dung-lai-0001");
            Assert.Equal("SUCCESS", (await SendAsync(first, nonce)).Code);

            var replay = NewRequest();
            _client.Sign(replay, Body, Now, nonce: "nonce-dung-lai-0001");
            var (response, code, _) = await SendAsync(replay, nonce);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("NONCE_REUSED", code);
        }

        [Fact]
        public async Task ChuKySai_KhongDotNonce()
        {
            var forged = NewRequest();
            new FastApiV3Signer(new byte[32]).Sign(forged, Body, Now, nonce: "nonce-bi-gia-mao-01");
            Assert.Equal("INVALID_SIGNATURE", (await SendAsync(forged, "nonce-bi-gia-mao-01")).Code);

            var genuine = NewRequest();
            string nonce = _client.Sign(genuine, Body, Now, nonce: "nonce-bi-gia-mao-01");
            Assert.Equal("SUCCESS", (await SendAsync(genuine, nonce)).Code);
        }

        [Theory]
        [InlineData("ngan")]
        [InlineData("co dau cach trong nonce")]
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
            HttpResponseMessage response = await _http.GetAsync("/api/v3/_meta/server");

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

        private sealed class FakeTime(long unixSeconds) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        private sealed class FakeKeyStore : ISignerKeyStore
        {
            private readonly Dictionary<string, SignerKeys> _keys = new();
            public void Add(SignerKeys keys) => _keys[keys.Name] = keys;
            public Task<SignerKeys?> GetAsync(string userName, CancellationToken cancellationToken = default) =>
                Task.FromResult(_keys.GetValueOrDefault(userName));
        }
    }
}
