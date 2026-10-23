using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Web.Custom;

namespace Tests
{
    /// <summary>
    /// Portal ApiDocs (KE_HOACH_V3 5.3): CORS cho trình duyệt ký request từ origin khác, và code mẫu dùng đúng vector thử
    /// </summary>
    public class PortalDocsTests
    {
        private const string PortalOrigin = "https://docs.congty.vn";

        private static async Task<IHost> StartAsync(string[] origins)
        {
            return await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services => services.AddPortalCors(origins))
                    .Configure(app =>
                    {
                        app.UseMiddleware<RequestIdMiddleware>();
                        app.UsePortalCors(origins);
                        // Thay cho SignatureMiddleware: request không token bị từ chối
                        app.Run(context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.Headers[SignatureMiddleware.SignatureHeader] = "abc";
                            return Task.CompletedTask;
                        });
                    }))
                .StartAsync();
        }

        private static HttpRequestMessage Preflight(string origin, string path = "/api/v3/sale-invoice")
        {
            var request = new HttpRequestMessage(HttpMethod.Options, path);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-fast-timestamp,x-fast-nonce,x-fast-content-sha256,x-fast-signature,idempotency-key");
            return request;
        }

        [Fact]
        public async Task Preflight_OriginDuocKhai_TraNgayKhongQuaChuKy()
        {
            using var host = await StartAsync([PortalOrigin + "/"]);
            var response = await host.GetTestClient().SendAsync(Preflight(PortalOrigin));

            Assert.Equal(204, (int)response.StatusCode);
            Assert.Equal(PortalOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            string allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
            Assert.Contains("x-fast-signature", allowed);
            Assert.Contains("idempotency-key", allowed);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }

        [Fact]
        public async Task Preflight_OriginLa_KhongCoHeaderCors()
        {
            using var host = await StartAsync([PortalOrigin]);
            var response = await host.GetTestClient().SendAsync(Preflight("https://evil.example"));

            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        [Fact]
        public async Task ResponseThat_ChoTrinhDuyetDocHeaderChuKy()
        {
            using var host = await StartAsync([PortalOrigin]);
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/sale-invoice");
            request.Headers.Add("Origin", PortalOrigin);
            var response = await host.GetTestClient().SendAsync(request);

            Assert.Equal(401, (int)response.StatusCode);
            string exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"));
            foreach (string header in PortalCors.ExposedHeaders)
            {
                Assert.Contains(header, exposed, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Theory]
        [InlineData(new string[0], "/api/v3/sale-invoice")]
        [InlineData(new[] { PortalOrigin }, "/api/sale-invoice")]
        public async Task KhongKhaiOrigin_HoacNgoaiV3_KhongMoCors(string[] origins, string path)
        {
            using var host = await StartAsync(origins);
            var response = await host.GetTestClient().SendAsync(Preflight(PortalOrigin, path));

            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.Equal(401, (int)response.StatusCode);
        }

        public static TheoryData<string> SampleFiles() => new()
        {
            "js/fast-sign.js", "samples/fast-sign.sh", "samples/FastSign.cs", "samples/fast-sign.mjs", "samples/fast_sign.py", "samples/FastSign.java"
        };

        /// <summary>
        /// Code mẫu và portal tự kiểm bằng cùng vector với MessageSignatureTests: đổi giao thức mà quên đổi mẫu thì test này đỏ
        /// </summary>
        [Theory]
        [MemberData(nameof(SampleFiles))]
        public void CodeMau_DungChungVectorThu(string file)
        {
            string text = File.ReadAllText(Path.Combine(SolutionDirectory(), "ApiDocs", "wwwroot", file));

            foreach (string value in new[]
            {
                MessageSignatureTests.PrivateKeyBase64, MessageSignatureTests.BodySha256, MessageSignatureTests.Nonce,
                MessageSignatureTests.IdempotencyKey, MessageSignatureTests.RequestSignature, MessageSignatureTests.RequestId,
                MessageSignatureTests.ResponseSignature, "1792574400", "1792574401"
            })
            {
                Assert.Contains(value, text);
            }
        }

        private static string SolutionDirectory()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "RestApiCore.sln")))
            {
                directory = directory.Parent;
            }
            return directory?.FullName ?? throw new DirectoryNotFoundException("Không thấy RestApiCore.sln");
        }
    }
}
