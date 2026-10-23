using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Business;
using Business.V3;
using DataAccess;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Models;
using Models.V3;
using Web.Custom;

namespace Tests
{
    /// <summary>
    /// V3FormEndpoint qua TestServer: chữ ký coi như đã kiểm (middleware giả gắn signer + rawBody), executor và kiểm đơn vị là giả
    /// </summary>
    public sealed class V3FormEndpointTests : IAsyncLifetime
    {
        private const string SaleInvoice = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="Voucher" voucherCode="HDA" unitRequired="true">
              <fields name="master" key="YourID">
                <field name="YourID" fastName="id_doitac" maxLength="10" allowNull="false"/>
                <field name="CustomerID" fastName="ma_kh"/>
              </fields>
              <fields name="detail">
                <field name="ItemID" fastName="ma_vt" allowNull="false"/>
              </fields>
              <query><text>select 1</text></query>
            </import>
            """;

        private const string GetCustomer = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="List">
              <fields name="filter"><field name="Keyword" fastName="tu_khoa"/></fields>
              <query><text>select 1</text></query>
            </import>
            """;

        private readonly FakeExecutor _executor = new();
        private readonly FakeUnitRights _units = new();
        private SignerKeys _signer = new(7, "DOITAC", true, new byte[32], new byte[32], 1, "ADMIN") { AllowForms = new HashSet<string>(["setSaleInvoice", "getCustomer"], StringComparer.OrdinalIgnoreCase) };
        private IHost _host = null!;
        private HttpClient _http = null!;

        public async Task InitializeAsync()
        {
            ImportForm Load(string name, string xml) => ColumnTypeResolver.Resolve(ImportFormLoader.Parse(name, XDocument.Parse(xml)), _ => null);
            var catalog = ImportFormCatalog.Build([Load("setSaleInvoice", SaleInvoice), Load("getCustomer", GetCustomer)]);

            _host = await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        var appConfig = new AppConfig { pathDirectory = ApiDirectory(), maxDocumentsPerRequest = 5 };
                        services.AddSingleton(appConfig);
                        services.AddMemoryCache();
                        services.AddSingleton<Models.Options.Options>(new OptionsRepository(appConfig));
                        services.AddSingleton<ResponseManager>();
                        services.AddSingleton(new V3FormRuntime(catalog, null));
                        services.AddSingleton<IFormExecutor>(_executor);
                        services.AddSingleton<IUnitRightsChecker>(_units);
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseMiddleware<RequestIdMiddleware>();
                        // Thay SignatureMiddleware: gắn signer và body thô như sau khi kiểm chữ ký thành công
                        app.Use(async (context, next) =>
                        {
                            using MemoryStream memory = new();
                            await context.Request.Body.CopyToAsync(memory);
                            context.Items[SignatureMiddleware.RawBodyItem] = memory.ToArray();
                            context.Items[SignatureMiddleware.SignerItem] = _signer;
                            await next(context);
                        });
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapV3Forms());
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

        private async Task<(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Response)> PostAsync(string path, string body, HttpMethod? method = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Post, "http://localhost" + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            HttpResponseMessage response = await _http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone(), response);
        }

        private const string ValidInvoice = """{ "master": { "YourID": "HD001", "CustomerID": "KH01" }, "detail": [ { "ItemID": "VT01" } ] }""";

        [Fact]
        public async Task KhongCoRoute_404()
        {
            var (status, json, _) = await PostAsync("/api/v3/khong-co", "{}");

            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("NOT_FOUND", json.GetProperty("code").GetString());
        }

        [Fact]
        public async Task SaiMethod_405_CoAllow()
        {
            var (status, json, response) = await PostAsync("/api/v3/customer/get", "{}", HttpMethod.Get);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, status);
            Assert.Equal("METHOD_NOT_ALLOWED", json.GetProperty("code").GetString());
            Assert.Contains("POST", response.Content.Headers.Allow);
        }

        [Fact]
        public async Task KhongCoQuyenForm_Forbidden()
        {
            _signer = _signer with { AllowForms = new HashSet<string>(["getCustomer"]) };

            var (status, json, _) = await PostAsync("/api/v3/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("FORBIDDEN", json.GetProperty("code").GetString());
            Assert.Null(_executor.LastCall);
        }

        [Fact]
        public async Task UnitRequiredThieuUnitCode_UnitRequired()
        {
            var (status, json, _) = await PostAsync("/api/v3/sale-invoice", ValidInvoice);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("UNIT_REQUIRED", json.GetProperty("code").GetString());
        }

        [Theory]
        [InlineData("KHAC", "UNIT_DENIED", HttpStatusCode.Forbidden)]
        [InlineData("KHONGCO", "UNIT_NOT_FOUND", HttpStatusCode.BadRequest)]
        public async Task UnitCodeKhongHopLe(string unit, string code, HttpStatusCode expected)
        {
            var (status, json, _) = await PostAsync($"/api/v3/sale-invoice?unitCode={unit}", ValidInvoice);

            Assert.Equal(expected, status);
            Assert.Equal(code, json.GetProperty("code").GetString());
            Assert.Equal(("KHAC" == unit ? "KHAC" : "KHONGCO", "New"), (_units.LastUnit, _units.LastAction));
        }

        [Fact]
        public async Task UnitCodeKhongBatBuocNhungCoGui_VanKiem()
        {
            var (_, json, _) = await PostAsync("/api/v3/customer/get?unitCode=KHAC", "{}");

            Assert.Equal("UNIT_DENIED", json.GetProperty("code").GetString());
            Assert.Equal("Acc", _units.LastAction);
        }

        [Fact]
        public async Task BodySaiDang_InvalidShape()
        {
            var (status, json, _) = await PostAsync("/api/v3/sale-invoice?unitCode=CTY", """{ "YourID": "HD001" }""");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("INVALID_SHAPE", json.GetProperty("code").GetString());
            Assert.Contains("thiếu khối gốc", json.GetProperty("message").GetString());
        }

        [Fact]
        public async Task LoiGiaTri_ValidationFailed_CoViTriVaMessage()
        {
            var (status, json, _) = await PostAsync("/api/v3/sale-invoice?unitCode=CTY", """{ "master": { "YourID": "QUA_DAI_HON_10" }, "detail": [ {} ] }""");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("VALIDATION_FAILED", json.GetProperty("code").GetString());
            var errors = json.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("position").GetString(), e.GetProperty("code").GetString())).ToList();
            Assert.Equal([("[0].master.YourID", "VALUE_TOO_LONG"), ("[0].detail[0].ItemID", "FIELD_REQUIRED")], errors);
            Assert.Equal("Field is required", json.GetProperty("errors")[1].GetProperty("message").GetString());
            Assert.Null(_executor.LastCall);
        }

        [Fact]
        public async Task LoiChecking_ValidationFailed()
        {
            _executor.Next = new FormExecutionResult { CheckingErrors = [new CheckingError("[0].detail[0].ItemID", "detail", "ItemID", "ITEM_NOT_FOUND")], RolledBack = true };

            var (status, json, _) = await PostAsync("/api/v3/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("ITEM_NOT_FOUND", json.GetProperty("errors")[0].GetProperty("code").GetString());
            Assert.Equal("ITEM_NOT_FOUND", json.GetProperty("errors")[0].GetProperty("message").GetString());
        }

        [Fact]
        public async Task DryRun_KhongTraDataset()
        {
            _executor.Next = new FormExecutionResult { RolledBack = true };

            var (status, json, _) = await PostAsync("/api/v3/sale-invoice?unitCode=CTY&dryRun=true", ValidInvoice);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(json.GetProperty("dryRun").GetBoolean());
            Assert.True(_executor.LastCall!.DryRun);
            Assert.Equal("CTY", _executor.LastCall.Unit);
            Assert.Equal(1, _executor.LastCall.UserId);
            Assert.Equal("ADMIN", _executor.LastCall.UserNameErp);
        }

        [Fact]
        public async Task ThanhCong_TenBangTheoCotTables()
        {
            _executor.Next = new FormExecutionResult { Data = DataSetOf(("SUCCESS", "", "master,detail"), 2) };

            var (status, json, _) = await PostAsync("/api/v3/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("HD001", json.GetProperty("data").GetProperty("master")[0].GetProperty("id").GetString());
            Assert.Equal(1, json.GetProperty("data").GetProperty("detail").GetArrayLength());
            Assert.Equal(1, _executor.LastBody!.Data.Tables["detail"]!.Rows.Count);
        }

        [Theory]
        [InlineData(1, "data")]
        [InlineData(2, "data2")]
        public async Task ThanhCong_KhongKhaiTables_TenMacDinh(int tableCount, string name)
        {
            _executor.Next = new FormExecutionResult { Data = DataSetOf(("SUCCESS", "", null), tableCount) };

            var (_, json, _) = await PostAsync("/api/v3/customer/get", "{}");

            Assert.True(json.GetProperty("data").TryGetProperty(name, out _));
        }

        [Theory]
        [InlineData("UNIT_DENIED", HttpStatusCode.Forbidden, "You do not have permission on this unit")]
        [InlineData("DUPLICATE_VOUCHER", HttpStatusCode.BadRequest, "Chứng từ đã tồn tại")]
        public async Task ProcessingTraCodeLoi_StatusTheoMessageXml(string code, HttpStatusCode expected, string message)
        {
            _executor.Next = new FormExecutionResult { Data = DataSetOf((code, code == "DUPLICATE_VOUCHER" ? message : "", null), 0), RolledBack = true };

            var (status, json, _) = await PostAsync("/api/v3/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(expected, status);
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal(message, json.GetProperty("message").GetString());
        }

        private static DataSet DataSetOf((string Code, string Message, string? Tables) head, int dataTables)
        {
            DataSet data = new();
            DataTable first = new("Table");
            first.Columns.Add("code");
            first.Columns.Add("message");
            if (head.Tables != null)
            {
                first.Columns.Add("tables");
                first.Rows.Add(head.Code, head.Message, head.Tables);
            }
            else
            {
                first.Rows.Add(head.Code, head.Message);
            }
            data.Tables.Add(first);
            for (int i = 0; i < dataTables; i++)
            {
                DataTable table = new($"Table{i + 1}");
                table.Columns.Add("id");
                table.Rows.Add("HD001");
                data.Tables.Add(table);
            }
            return data;
        }

        private static string ApiDirectory()
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Api", "App_Data", "Options", "Message.xml")))
            {
                dir = dir.Parent;
            }
            return Path.Combine(dir!.FullName, "Api");
        }

        private sealed class FakeExecutor : IFormExecutor
        {
            public FormExecutionResult Next { get; set; } = new() { Data = new DataSet() };
            public FormCallContext? LastCall { get; private set; }
            public MappedBody? LastBody { get; private set; }

            public Task<FormExecutionResult> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default)
            {
                LastCall = call;
                LastBody = body;
                return Task.FromResult(Next);
            }
        }

        /// <summary>CTY được phép, KHONGCO không tồn tại, còn lại bị từ chối</summary>
        private sealed class FakeUnitRights : IUnitRightsChecker
        {
            public string? LastUnit { get; private set; }
            public string? LastAction { get; private set; }

            public Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default)
            {
                LastUnit = unitCode;
                LastAction = action;
                return Task.FromResult(unitCode switch { "CTY" => "SUCCESS", "KHONGCO" => "UNIT_NOT_FOUND", _ => "UNIT_DENIED" });
            }
        }
    }

    public class FormVariablesTests
    {
        [Fact]
        public void Replace_ChuoiThanhLiteral_SoGiuNguyen_TenDbThanhDinhDanh()
        {
            ImportForm form = ImportFormLoader.Parse("setSaleInvoice", XDocument.Parse("""
                <import xmlns="urn:schemas-fast-com:fast-api" type="Voucher" voucherCode="HDA">
                  <fields name="master" key="YourID"><field name="YourID" fastName="id_doitac" isPrimaryKey="true"/></fields>
                  <query><text>select 1</text></query>
                </import>
                """));
            var call = new FormCallContext(7, "DOITAC", 1, "ADMIN    ", true, "CT'Y", false);
            var config = new AppConfig { appDatabaseName = "FSD_A", sysDatabaseName = "FSD]S" };

            string sql = FormVariables.Replace(
                "exec p @@voucherCode, @@unit, @@userId, @@userName, @@admin, @@form, @@verb, @@resource, @@type; select * from @@sysDatabaseName..x, @@appDatabaseName..y; select @@primaryKeyListAlias; @@khongCo @@ROWCOUNT",
                form, call, config);

            Assert.Equal(
                "exec p N'HDA', N'CT''Y', 1, N'ADMIN', 1, N'setSaleInvoice', N'set', N'sale-invoice', N'Voucher'; select * from [FSD]]S]..x, [FSD_A]..y; select id_doitac AS YourID; @@khongCo @@ROWCOUNT",
                sql);
        }

        [Fact]
        public void ToError_ViTriTheoKhoi()
        {
            DataTable table = new();
            foreach (string column in new[] { "block", "row_no", "parent_no", "field", "code" })
            {
                table.Columns.Add(column);
            }
            table.Rows.Add("master", 1, 1, "CustomerID", "CUSTOMER_NOT_FOUND");
            table.Rows.Add("detail", 2, 0, "ItemID", "ITEM_NOT_FOUND");

            Assert.Equal("[1].master.CustomerID", FormExecutor.ToError(table.Rows[0], "master").Position);
            Assert.Equal("[0].detail[2].ItemID", FormExecutor.ToError(table.Rows[1], "master").Position);
        }
    }
}
