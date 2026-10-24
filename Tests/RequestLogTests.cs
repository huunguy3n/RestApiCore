using System.Text;
using Business.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Models;
using Web.Custom;

namespace Tests
{
    public sealed class RequestLogMiddlewareTests : IAsyncLifetime
    {
        private readonly RequestLogQueue _queue = new(new AppConfig { logQueueCapacity = 100 });
        private IHost _host = null!;
        private HttpClient _http = null!;

        public async Task InitializeAsync()
        {
            _host = await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(new AppConfig { logMaxBodyBytes = 64, maxRequestBodyBytes = 1024 * 1024 });
                        services.AddSingleton(_queue);
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<RequestIdMiddleware>();
                        app.UseMiddleware<RequestLogMiddleware>();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/api/customer/get", async context =>
                            {
                                context.Items[RequestLogMiddleware.SignatureResultItem] = "VALID";
                                context.Items[RequestLogMiddleware.FormItem] = "getCustomer";
                                context.Response.ContentType = "application/json";
                                await context.Response.WriteAsync("""{"code":"SUCCESS","data":{"code":"bên trong"}}""");
                            });
                            endpoints.MapPost("/api/auth/token", async context =>
                            {
                                context.Response.ContentType = "application/json";
                                await context.Response.WriteAsync("""{"code":"SUCCESS","accessToken":"eyJ.bi-mat.xyz"}""");
                            });
                            endpoints.MapPost("/api/sale-invoice", async context =>
                            {
                                context.Response.StatusCode = 401;
                                context.Response.ContentType = "application/json; charset=utf-8";
                                await context.Response.WriteAsync("""{"code":"INVALID_SIGNATURE","success":false}""");
                            });
                            endpoints.MapPost("/api/query/old", async context =>
                            {
                                // Route cũ đọc body như MVC
                                using var reader = new StreamReader(context.Request.Body);
                                await context.Response.WriteAsync("ok:" + await reader.ReadToEndAsync());
                            });
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

        private async Task<(HttpResponseMessage Response, RequestLogEntry Entry)> PostAsync(string path, string body, Action<HttpRequestMessage>? setup = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost" + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            setup?.Invoke(request);
            HttpResponseMessage response = await _http.SendAsync(request);
            await response.Content.ReadAsStringAsync();
            Assert.True(_queue.Reader.TryRead(out RequestLogEntry? entry));
            return (response, entry!);
        }

        [Fact]
        public async Task GhiDuThongTin_KhongGhiHeaderBiMat()
        {
            var (response, entry) = await PostAsync("/api/customer/get?unitCode=CTY", "{}", request =>
            {
                request.Headers.Add("X-Fast-Nonce", "1792574400-0123456789ab");
                request.Headers.Add("X-Fast-Signature", "chu-ky-bi-mat");
                request.Headers.Add("Idempotency-Key", "key-1");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer jwt-bi-mat");
            });

            Assert.Equal(Guid.Parse(response.Headers.GetValues("X-Request-ID").Single()), entry.ReqId);
            Assert.Equal(("POST", "/api/customer/get", "?unitCode=CTY"), (entry.Method, entry.Path, entry.QueryString));
            Assert.Equal((200, "SUCCESS", "getCustomer", "VALID"), (entry.StatusCode, entry.ResultCode, entry.Form, entry.SignatureResult));
            Assert.Equal(("1792574400-0123456789ab", 1792574400L, "key-1"), (entry.Nonce, entry.ClientTimestamp, entry.IdempotencyKey));
            Assert.Equal("{}", Encoding.UTF8.GetString(entry.RequestBody!));
            Assert.False(entry.IsAttack);

            string serialized = System.Text.Json.JsonSerializer.Serialize(entry);
            Assert.DoesNotContain("chu-ky-bi-mat", serialized);
            Assert.DoesNotContain("jwt-bi-mat", serialized);
        }

        [Fact]
        public async Task AuthToken_KhongLuuBodyResponseChuaJwt()
        {
            var (_, entry) = await PostAsync("/api/auth/token", """{"userName":"DOITAC"}""");

            Assert.Null(entry.ResponseBody);
            Assert.Null(entry.ResponseSha256);
            Assert.True(entry.ResponseBytes > 0);
            Assert.Equal("""{"userName":"DOITAC"}""", Encoding.UTF8.GetString(entry.RequestBody!));
        }

        [Fact]
        public async Task MaTanCong_DanhDauAttack()
        {
            var (_, entry) = await PostAsync("/api/sale-invoice", "{}");

            Assert.Equal((401, "INVALID_SIGNATURE"), (entry.StatusCode, entry.ResultCode));
            Assert.True(entry.IsAttack);
        }

        [Fact]
        public async Task BodyLon_ChiGiuSha256_RouteCuVanDocDuocBody()
        {
            string big = new('x', 100);
            var (response, entry) = await PostAsync("/api/query/old", big);

            Assert.Equal("ok:" + big, await response.Content.ReadAsStringAsync());
            Assert.Null(entry.RequestBody);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(big))), entry.RequestSha256);
            Assert.Equal(100, entry.RequestBytes);
            Assert.Null(entry.ResultCode);
        }

        [Theory]
        [InlineData("application/json", """{"success":false,"data":{"code":"X"},"code":"NOT_FOUND"}""", "NOT_FOUND")]
        [InlineData("application/json", """[{"code":"X"}]""", null)]
        [InlineData("text/plain", """{"code":"X"}""", null)]
        [InlineData("application/json", """{"code":""", null)]
        public void ResultCode_ChiLayCapGoc(string contentType, string body, string? expected)
        {
            Assert.Equal(expected, RequestLogMiddleware.ResultCode(contentType, Encoding.UTF8.GetBytes(body)));
        }
    }

    public class LogWorkerTests
    {
        private static RequestLogEntry Entry(int i) => new()
        {
            ReqId = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            Method = "POST",
            Path = $"/api/x{i}",
            StatusCode = 200,
            DurationMs = i,
            RequestBody = Encoding.UTF8.GetBytes($"{{\"i\":{i}}}")
        };

        private sealed class FakeSink : IRequestLogSink
        {
            public bool Fail { get; set; }
            public List<List<RequestLogEntry>> Batches { get; } = [];
            public int Cleanups { get; private set; }

            public Task WriteAsync(IReadOnlyList<RequestLogEntry> entries, CancellationToken cancellationToken = default)
            {
                if (Fail)
                {
                    throw new InvalidOperationException("SQL tắt");
                }
                lock (Batches)
                {
                    Batches.Add([.. entries]);
                }
                return Task.CompletedTask;
            }

            public Task<(int Requests, int Bodies, int Attacks)> CleanupAsync(int retentionDays, int attackRetentionDays, CancellationToken cancellationToken = default)
            {
                Cleanups++;
                return Task.FromResult((0, 0, 0));
            }

            public int Count { get { lock (Batches) { return Batches.Sum(b => b.Count); } } }
        }

        [Fact]
        public async Task GhiTheoLo_GioiHanKichThuocLo()
        {
            var config = new AppConfig { logQueueCapacity = 1000, logBatchSize = 10 };
            var queue = new RequestLogQueue(config);
            var sink = new FakeSink();
            string dir = Directory.CreateTempSubdirectory("fastapi-spool-").FullName;
            using var worker = new LogWorker(queue, sink, new RequestLogSpool(dir), config, NullLogger<LogWorker>.Instance) { FlushInterval = TimeSpan.FromMilliseconds(50) };
            try
            {
                for (int i = 0; i < 25; i++)
                {
                    queue.Enqueue(Entry(i));
                }
                await worker.StartAsync(CancellationToken.None);

                // Dọn log chạy sau khi ghi lô cuối, đợi cả hai
                Assert.True(await WaitUntil(() => sink.Count == 25 && sink.Cleanups > 0));
                Assert.All(sink.Batches, b => Assert.True(b.Count <= 10));
                Assert.Equal(1, sink.Cleanups);
                await worker.StopAsync(CancellationToken.None);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task SqlTat_Spool_SqlLenLai_DayLaiVaXoaFile()
        {
            var config = new AppConfig { logQueueCapacity = 1000, logBatchSize = 100 };
            var queue = new RequestLogQueue(config);
            var sink = new FakeSink { Fail = true };
            string dir = Directory.CreateTempSubdirectory("fastapi-spool-").FullName;
            var spool = new RequestLogSpool(dir);
            using var worker = new LogWorker(queue, sink, spool, config, NullLogger<LogWorker>.Instance) { FlushInterval = TimeSpan.FromMilliseconds(50) };
            try
            {
                await worker.StartAsync(CancellationToken.None);
                for (int i = 0; i < 5; i++)
                {
                    queue.Enqueue(Entry(i));
                }
                Assert.True(await WaitUntil(() => spool.Pending().Count > 0), "SQL tắt → có file spool");
                Assert.Equal(0, sink.Count);

                sink.Fail = false;
                queue.Enqueue(Entry(99));

                Assert.True(await WaitUntil(() => sink.Count == 6 && spool.Pending().Count == 0), "SQL lên → đẩy lại spool, xoá file");
                Assert.Equal(Enumerable.Range(0, 5).Select(i => $"/api/x{i}").Append("/api/x99").Order(),
                    sink.Batches.SelectMany(b => b).Select(e => e.Path).Order());
                await worker.StopAsync(CancellationToken.None);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Dung_GhiNotHangDoi()
        {
            var config = new AppConfig { logQueueCapacity = 1000, logBatchSize = 100 };
            var queue = new RequestLogQueue(config);
            var sink = new FakeSink();
            string dir = Directory.CreateTempSubdirectory("fastapi-spool-").FullName;
            using var worker = new LogWorker(queue, sink, new RequestLogSpool(dir), config, NullLogger<LogWorker>.Instance) { FlushInterval = TimeSpan.FromSeconds(30) };
            try
            {
                await worker.StartAsync(CancellationToken.None);
                await Task.Delay(100);
                for (int i = 0; i < 3; i++)
                {
                    queue.Enqueue(Entry(i));
                }
                await worker.StopAsync(CancellationToken.None);

                Assert.Equal(3, sink.Count);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void HangDoiDay_BoMucMoi_DemSoBo()
        {
            var queue = new RequestLogQueue(new AppConfig { logQueueCapacity = 2 });
            for (int i = 0; i < 5; i++)
            {
                queue.Enqueue(Entry(i));
            }
            Assert.Equal(3, queue.Dropped);
        }

        [Fact]
        public void Spool_GhiDocLai_DayDu()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-spool-").FullName;
            try
            {
                var spool = new RequestLogSpool(dir);
                RequestLogEntry entry = Entry(1) with { UId = 7, ClientTimestamp = 1792574400, IsAttack = true, ResultCode = "NONCE_REUSED" };

                string file = spool.Write([entry, Entry(2)]);
                List<RequestLogEntry> read = RequestLogSpool.Read(file);

                Assert.Equal([file], spool.Pending());
                Assert.Equal(2, read.Count);
                Assert.Equal(entry with { RequestBody = null }, read[0] with { RequestBody = null });
                Assert.Equal(entry.RequestBody, read[0].RequestBody);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static async Task<bool> WaitUntil(Func<bool> condition)
        {
            for (int i = 0; i < 100 && !condition(); i++)
            {
                await Task.Delay(50);
            }
            return condition();
        }
    }
}
