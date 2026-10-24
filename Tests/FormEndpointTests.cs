using System.Data;
using System.Net;
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
using Web.Custom;

namespace Tests
{
    /// <summary>
    /// FormEndpoint qua TestServer: chữ ký coi như đã kiểm (middleware giả gắn signer + rawBody), executor và kiểm đơn vị là giả
    /// </summary>
    public sealed class FormEndpointTests : IAsyncLifetime
    {
        private const string SaleInvoice = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="Voucher" voucherCode="HDA" unitRequired="true">
              <fields name="master" key="yourId">
                <field name="yourId" fastName="id_doitac" maxLength="10" allowNull="false"/>
                <field name="customerId" fastName="ma_kh"/>
              </fields>
              <fields name="detail">
                <field name="itemId" fastName="ma_vt" allowNull="false"/>
              </fields>
              <query><text>select 1</text></query>
            </import>
            """;

        private const string GetInvoice = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="Report">
              <fields name="filter"><field name="year" fastName="nam" type="int"/></fields>
              <paging pageSize="2" key="id"/>
              <query><text>select 1</text></query>
            </import>
            """;

        private const string TransferInvoice = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="Transfer">
              <fields name="filter"><field name="year" fastName="nam" type="int"/></fields>
              <paging pageSize="2" key="id"/>
              <query><text>select 1</text></query>
              <query action="transferred"><text>update x set transfer_yn = 1 from x join #transferred b on b.id = x.id</text></query>
            </import>
            """;

        private const string GetCustomer = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="List">
              <fields name="filter"><field name="keyword" fastName="tu_khoa"/></fields>
              <query><text>select 1</text></query>
            </import>
            """;

        private readonly FakeExecutor _executor = new();
        private readonly FakeUnitRights _units = new();
        private readonly FakeIdempotency _idempotency = new();
        private SignerKeys _signer = new(7, "DOITAC", true, new byte[32], new byte[32], 1, "ADMIN") { AllowForms = new HashSet<string>(["setSaleInvoice", "getCustomer", "getInvoice", "transferInvoice"], StringComparer.OrdinalIgnoreCase) };
        private bool _withoutSigner;
        private IHost _host = null!;
        private HttpClient _http = null!;

        public async Task InitializeAsync()
        {
            ImportForm Load(string name, string xml) => ColumnTypeResolver.Resolve(ImportFormLoader.Parse(name, XDocument.Parse(xml)), _ => null);
            var catalog = ImportFormCatalog.Build([Load("setSaleInvoice", SaleInvoice), Load("getCustomer", GetCustomer), Load("getInvoice", GetInvoice), Load("transferInvoice", TransferInvoice)]);

            _host = await new HostBuilder()
                .ConfigureWebHost(web => web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        var appConfig = new AppConfig { pathDirectory = ApiDirectory(), maxDocumentsPerRequest = 5, maxRowsWithoutPaging = 50, cursorTtlMinutes = 5 };
                        services.AddSingleton(appConfig);
                        services.AddMemoryCache();
                        services.AddSingleton<Models.Options.Options>(new OptionsRepository(appConfig));
                        services.AddSingleton<ResponseManager>();
                        services.AddSingleton(new FormRuntime(catalog, null));
                        services.AddSingleton<IFormExecutor>(_executor);
                        services.AddSingleton<IUnitRightsChecker>(_units);
                        services.AddSingleton<IIdempotencyStore>(_idempotency);
                        services.AddHybridCache();
                        services.AddSingleton<ICursorStore, CursorStore>();
                        services.AddSingleton<FormRequestService>();
                        services.AddSingleton<FormResponseWriter>();
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
                            if (!_withoutSigner)
                            {
                                context.Items[SignatureMiddleware.SignerItem] = _signer;
                            }
                            await next(context);
                        });
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapForms());
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

        private async Task<(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Response)> PostAsync(string path, string body, HttpMethod? method = null, string? idempotencyKey = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Post, "http://localhost" + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (idempotencyKey != null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }
            HttpResponseMessage response = await _http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone(), response);
        }

        private const string ValidInvoice = """{ "master": { "yourId": "HD001", "customerId": "KH01" }, "detail": [ { "itemId": "VT01" } ] }""";

        [Fact]
        public async Task KhongCoRoute_404()
        {
            var (status, json, _) = await PostAsync("/api/khong-co", "{}");

            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("NOT_FOUND", json.GetProperty("code").GetString());
        }

        [Fact]
        public async Task SaiMethod_405_CoAllow()
        {
            var (status, json, response) = await PostAsync("/api/customer/get", "{}", HttpMethod.Get);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, status);
            Assert.Equal("METHOD_NOT_ALLOWED", json.GetProperty("code").GetString());
            Assert.Contains("POST", response.Content.Headers.Allow);
        }

        [Fact]
        public async Task KhongCoQuyenForm_Forbidden()
        {
            _signer = _signer with { AllowForms = new HashSet<string>(["getCustomer"]) };

            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("FORBIDDEN", json.GetProperty("code").GetString());
            Assert.Null(_executor.LastCall);
        }

        [Fact]
        public async Task UnitRequiredThieuUnitCode_UnitRequired()
        {
            var (status, json, _) = await PostAsync("/api/sale-invoice", ValidInvoice);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("UNIT_REQUIRED", json.GetProperty("code").GetString());
        }

        [Theory]
        [InlineData("KHAC", "UNIT_DENIED", HttpStatusCode.Forbidden)]
        [InlineData("KHONGCO", "UNIT_NOT_FOUND", HttpStatusCode.BadRequest)]
        public async Task UnitCodeKhongHopLe(string unit, string code, HttpStatusCode expected)
        {
            var (status, json, _) = await PostAsync($"/api/sale-invoice?unitCode={unit}", ValidInvoice);

            Assert.Equal(expected, status);
            Assert.Equal(code, json.GetProperty("code").GetString());
            Assert.Equal(("KHAC" == unit ? "KHAC" : "KHONGCO", "New"), (_units.LastUnit, _units.LastAction));
        }

        [Fact]
        public async Task UnitCodeKhongBatBuocNhungCoGui_VanKiem()
        {
            var (_, json, _) = await PostAsync("/api/customer/get?unitCode=KHAC", "{}");

            Assert.Equal("UNIT_DENIED", json.GetProperty("code").GetString());
            Assert.Equal("Acc", _units.LastAction);
        }

        [Fact]
        public async Task BodySaiDang_InvalidShape()
        {
            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", """{ "yourId": "HD001" }""");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("INVALID_SHAPE", json.GetProperty("code").GetString());
            Assert.Contains("thiếu khối gốc", json.GetProperty("message").GetString());
        }

        [Fact]
        public async Task LoiGiaTri_ValidationFailed_CoViTriVaMessage()
        {
            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", """{ "master": { "yourId": "QUA_DAI_HON_10" }, "detail": [ {} ] }""");

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("VALIDATION_FAILED", json.GetProperty("code").GetString());
            var errors = json.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("position").GetString(), e.GetProperty("code").GetString())).ToList();
            Assert.Equal([("[0].master.yourId", "VALUE_TOO_LONG"), ("[0].detail[0].itemId", "FIELD_REQUIRED")], errors);
            Assert.Equal("Field is required", json.GetProperty("errors")[1].GetProperty("message").GetString());
            Assert.Null(_executor.LastCall);
        }

        [Fact]
        public async Task LoiChecking_ValidationFailed()
        {
            _executor.Next = new FormExecutionResult { CheckingErrors = [new CheckingError("[0].detail[0].itemId", "detail", "itemId", "ITEM_NOT_FOUND")], RolledBack = true };

            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("ITEM_NOT_FOUND", json.GetProperty("errors")[0].GetProperty("code").GetString());
            Assert.Equal("Item does not exist or is inactive", json.GetProperty("errors")[0].GetProperty("message").GetString());
        }

        [Fact]
        public async Task DryRun_KhongTraDataset()
        {
            _executor.Next = new FormExecutionResult { RolledBack = true };

            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY&dryRun=true", ValidInvoice);

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

            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("HD001", json.GetProperty("data").GetProperty("master")[0].GetProperty("id").GetString());
            Assert.Equal(1, json.GetProperty("data").GetProperty("detail").GetArrayLength());
            Assert.Equal(1, _executor.LastBody!.Data.Tables["detail"]!.Rows.Count);
        }

        [Fact]
        public async Task ProcessingTraCotKhongCamel_InvalidResponseFormat_KhongTraDuLieu()
        {
            DataSet data = DataSetOf(("SUCCESS", "", "master"), 1);
            data.Tables[1].Columns.Add("customer_name");
            _executor.Next = new FormExecutionResult { Data = data };

            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(HttpStatusCode.InternalServerError, status);
            Assert.Equal("INVALID_RESPONSE_FORMAT", json.GetProperty("code").GetString());
            Assert.Contains("master.customer_name", json.GetProperty("message").GetString());
            Assert.False(json.TryGetProperty("data", out _));
        }

        [Theory]
        [InlineData("""{ "master": { "YourId": "HD001", "customerId": "KH01" } }""", "VALIDATION_FAILED", "[0].master.YourId")]
        [InlineData("""{ "Master": { "yourId": "HD001", "customerId": "KH01" } }""", "INVALID_SHAPE", "Master")]
        [InlineData("""{ "master": { "yourId": "HD001", "customerId": "KH01" }, "Detail": [ { "itemId": "VT01" } ] }""", "INVALID_SHAPE", "Detail")]
        public async Task TenJsonSaiHoaThuong_BiTuChoi(string body, string code, string expected)
        {
            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", body);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(code, json.GetProperty("code").GetString());
            Assert.Contains(expected, json.ToString());
            Assert.Null(_executor.LastBody);
        }

        [Theory]
        [InlineData(1, "data")]
        [InlineData(2, "data2")]
        public async Task ThanhCong_KhongKhaiTables_TenMacDinh(int tableCount, string name)
        {
            _executor.Next = new FormExecutionResult { Data = DataSetOf(("SUCCESS", "", null), tableCount) };

            var (_, json, _) = await PostAsync("/api/customer/get", "{}");

            Assert.True(json.GetProperty("data").TryGetProperty(name, out _));
        }

        [Theory]
        [InlineData("UNIT_DENIED", HttpStatusCode.Forbidden, "You do not have permission on this unit")]
        [InlineData("DUPLICATE_VOUCHER", HttpStatusCode.BadRequest, "Chứng từ đã tồn tại")]
        public async Task ProcessingTraCodeLoi_StatusTheoMessageXml(string code, HttpStatusCode expected, string message)
        {
            _executor.Next = new FormExecutionResult { Data = DataSetOf((code, code == "DUPLICATE_VOUCHER" ? message : "", null), 0), RolledBack = true };

            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice);

            Assert.Equal(expected, status);
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal(message, json.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Idempotency_LanDau_ChayVaLuu_LanSau_TraLai()
        {
            _executor.Next = new FormExecutionResult { Data = DataSetOf(("SUCCESS", "", "master"), 1) };

            var first = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: "key-001");
            Assert.Equal(HttpStatusCode.OK, first.Status);
            Assert.Equal(1, _executor.Calls);
            Assert.Equal(200, _idempotency.Saved["key-001"].Status);

            var second = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: "key-001");

            Assert.Equal(HttpStatusCode.OK, second.Status);
            Assert.Equal(1, _executor.Calls);
            Assert.Equal(first.Json.GetRawText(), second.Json.GetRawText());
            Assert.Equal("true", second.Response.Headers.GetValues(FormEndpoint.ReplayedHeader).Single());
            Assert.False(first.Response.Headers.Contains(FormEndpoint.ReplayedHeader));
        }

        [Fact]
        public async Task Idempotency_LoiChecking_CungDuocLuu()
        {
            _executor.Next = new FormExecutionResult { CheckingErrors = [new CheckingError("[0].master.customerId", "master", "customerId", "CUSTOMER_NOT_FOUND")], RolledBack = true };

            await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: "key-002");
            var replay = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: "key-002");

            Assert.Equal(HttpStatusCode.BadRequest, replay.Status);
            Assert.Equal("VALIDATION_FAILED", replay.Json.GetProperty("code").GetString());
            Assert.Equal(1, _executor.Calls);
        }

        [Theory]
        [InlineData("?unitCode=CTY&dryRun=false", """{ "master": { "yourId": "HD999" }, "detail": [ { "itemId": "VT01" } ] }""")]
        [InlineData("?unitCode=KHAC2", null)]
        public async Task Idempotency_CungKeyKhacBodyHoacQuery_Conflict(string query, string? body)
        {
            _units.Allow("KHAC2");
            await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: "key-003");

            var (status, json, _) = await PostAsync("/api/sale-invoice" + query, body ?? ValidInvoice, idempotencyKey: "key-003");

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("IDEMPOTENCY_CONFLICT", json.GetProperty("code").GetString());
            Assert.Equal(1, _executor.Calls);
        }

        [Fact]
        public async Task Idempotency_DangChay_InProgress()
        {
            _idempotency.Pending("key-004");

            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: "key-004");

            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("IDEMPOTENCY_IN_PROGRESS", json.GetProperty("code").GetString());
        }

        [Fact]
        public async Task Idempotency_LoiKhongLuongTruoc_BoGiuCho()
        {
            _executor.Throw = true;

            await Assert.ThrowsAnyAsync<Exception>(() => PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: "key-005"));

            Assert.Contains("key-005", _idempotency.Released);
            Assert.False(_idempotency.Saved.ContainsKey("key-005"));
        }

        [Fact]
        public async Task Idempotency_DryRun_KhongDung()
        {
            await PostAsync("/api/sale-invoice?unitCode=CTY&dryRun=true", ValidInvoice, idempotencyKey: "key-006");

            Assert.Empty(_idempotency.Reserved);
        }

        [Fact]
        public async Task Idempotency_KeySaiDang_Invalid()
        {
            var (status, json, _) = await PostAsync("/api/sale-invoice?unitCode=CTY", ValidInvoice, idempotencyKey: new string('k', 129));

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("INVALID_IDEMPOTENCY_KEY", json.GetProperty("code").GetString());
        }

        [Fact]
        public void FormMau_NapDuoc_DungRoute()
        {
            string formDirectory = Path.Combine(ApiDirectory(), "App_Data", "Form");

            ImportForm sale = ImportFormLoader.Load(Path.Combine(formDirectory, "setSaleInvoice.xml"));
            ImportForm customer = ImportFormLoader.Load(Path.Combine(formDirectory, "getCustomer.xml"));

            Assert.Equal("/api/sale-invoice", sale.Route);
            Assert.True(sale.UnitRequired);
            Assert.Equal(["CUSTOMER_NOT_FOUND", "ITEM_NOT_FOUND", "INVALID_FIELD", "INVALID_QUANTITY"],
                sale.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Checks).Select(c => c.ErrorCode));
            Assert.Empty(sale.Warnings);
            Assert.Equal("/api/customer/get", customer.Route);
            Assert.False(customer.UnitRequired);
        }

        /// <summary>5 hóa đơn, mỗi hóa đơn 2 dòng chi tiết; bảng tổng không có cột id</summary>
        private static DataSet Invoices()
        {
            DataSet data = DataSetOf(("SUCCESS", "", "master,detail,summary"), 0);
            DataTable master = new("Table1"), detail = new("Table2"), summary = new("Table3");
            master.Columns.Add("id");
            detail.Columns.Add("id");
            detail.Columns.Add("line", typeof(int));
            summary.Columns.Add("total", typeof(int));
            for (int i = 0; i < 5; i++)
            {
                master.Rows.Add($"HD{i}");
                detail.Rows.Add($"HD{i}", 1);
                detail.Rows.Add($"HD{i}", 2);
            }
            summary.Rows.Add(5);
            data.Tables.AddRange([master, detail, summary]);
            return data;
        }

        [Fact]
        public async Task PhanTrang_Trang0CoCursor_TrangSauLayTuCursor()
        {
            _executor.Next = new FormExecutionResult { Data = Invoices() };

            var (status, first, _) = await PostAsync("/api/invoice/get?unitCode=CTY", "{}");

            Assert.Equal(HttpStatusCode.OK, status);
            JsonElement paging = first.GetProperty("paging");
            Assert.Equal(3, paging.GetProperty("pageCount").GetInt32());
            Assert.Equal(16, paging.GetProperty("totalRowCount").GetInt32());
            Assert.Equal(0, paging.GetProperty("pageIndex").GetInt32());
            Assert.Equal(["HD0", "HD1"], first.GetProperty("data").GetProperty("master").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
            Assert.Equal(4, first.GetProperty("data").GetProperty("detail").GetArrayLength());
            Assert.Equal(1, first.GetProperty("data").GetProperty("summary").GetArrayLength());
            string cursor = paging.GetProperty("cursor").GetString()!;

            var (_, last, _) = await PostAsync($"/api/invoice/get?unitCode=CTY&cursor={cursor}&page=2", "");

            Assert.Equal(1, _executor.Calls);
            Assert.Equal(["HD4"], last.GetProperty("data").GetProperty("master").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
            Assert.Equal(2, last.GetProperty("data").GetProperty("detail").GetArrayLength());
            Assert.Equal(0, last.GetProperty("data").GetProperty("summary").GetArrayLength());
            Assert.Equal(2, last.GetProperty("paging").GetProperty("pageIndex").GetInt32());
        }

        [Theory]
        [InlineData("page=3", "PAGE_OUT_OF_RANGE", HttpStatusCode.BadRequest)]
        [InlineData("page=-1", "PAGE_OUT_OF_RANGE", HttpStatusCode.BadRequest)]
        [InlineData("page=x", "PAGE_OUT_OF_RANGE", HttpStatusCode.BadRequest)]
        [InlineData("page=1&unitCode=KHAC2", "CURSOR_EXPIRED", HttpStatusCode.NotFound)]
        public async Task PhanTrang_ThamSoSai(string query, string code, HttpStatusCode expected)
        {
            _units.Allow("KHAC2");
            _executor.Next = new FormExecutionResult { Data = Invoices() };
            var (_, first, _) = await PostAsync("/api/invoice/get?unitCode=CTY", "{}");
            string cursor = first.GetProperty("paging").GetProperty("cursor").GetString()!;

            string path = query.Contains("unitCode") ? $"/api/invoice/get?cursor={cursor}&{query}" : $"/api/invoice/get?unitCode=CTY&cursor={cursor}&{query}";
            var (status, json, _) = await PostAsync(path, "");

            Assert.Equal(expected, status);
            Assert.Equal(code, json.GetProperty("code").GetString());
        }

        [Fact]
        public async Task PhanTrang_CursorKhongCo_HoacKhacForm_Expired()
        {
            var (status, json, _) = await PostAsync("/api/invoice/get?cursor=khongco&page=1", "");
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("CURSOR_EXPIRED", json.GetProperty("code").GetString());

            _executor.Next = new FormExecutionResult { Data = Invoices() };
            var (_, first, _) = await PostAsync("/api/invoice/get", "{}");
            string cursor = first.GetProperty("paging").GetProperty("cursor").GetString()!;
            var (_, other, _) = await PostAsync($"/api/customer/get?cursor={cursor}&page=1", "");
            Assert.Equal("CURSOR_EXPIRED", other.GetProperty("code").GetString());
        }

        [Fact]
        public async Task PhanTrang_MotTrang_KhongCursor()
        {
            DataSet data = DataSetOf(("SUCCESS", "", "master"), 0);
            DataTable master = new("Table1");
            master.Columns.Add("id");
            master.Rows.Add("HD0");
            data.Tables.Add(master);
            _executor.Next = new FormExecutionResult { Data = data };

            var (_, json, _) = await PostAsync("/api/invoice/get", "{}");

            Assert.Equal(JsonValueKind.Null, json.GetProperty("paging").GetProperty("cursor").ValueKind);
            Assert.Equal(1, json.GetProperty("paging").GetProperty("pageCount").GetInt32());
        }

        [Fact]
        public async Task KhongPhanTrang_QuaTranDong_ResultTooLarge()
        {
            DataSet data = DataSetOf(("SUCCESS", "", null), 0);
            DataTable rows = new("Table1");
            rows.Columns.Add("id");
            for (int i = 0; i < 51; i++)
            {
                rows.Rows.Add(i.ToString());
            }
            data.Tables.Add(rows);
            _executor.Next = new FormExecutionResult { Data = data };

            var (status, json, _) = await PostAsync("/api/customer/get", "{}");

            Assert.Equal(HttpStatusCode.InternalServerError, status);
            Assert.Equal("RESULT_TOO_LARGE", json.GetProperty("code").GetString());
            Assert.Contains("getCustomer", json.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Transfer_DanhDauDungKeyCuaTrangDaTra()
        {
            _executor.Next = new FormExecutionResult { Data = Invoices() };

            var (_, first, _) = await PostAsync("/api/invoice/transfer?unitCode=CTY", "{}");
            string cursor = first.GetProperty("paging").GetProperty("cursor").GetString()!;
            Assert.Equal(2, first.GetProperty("paging").GetProperty("transferredCount").GetInt32());
            Assert.Equal(["HD0", "HD1"], _executor.Marked.Single().Keys);
            Assert.Equal("CTY", _executor.Marked.Single().Unit);

            var (_, last, _) = await PostAsync($"/api/invoice/transfer?unitCode=CTY&cursor={cursor}&page=2", "");

            Assert.Equal(["HD4"], _executor.Marked[1].Keys);
            Assert.Equal("CTY", _executor.Marked[1].Unit);
            Assert.Equal(1, last.GetProperty("paging").GetProperty("transferredCount").GetInt32());
            Assert.Equal(2, _executor.Marked.Count);
        }

        [Fact]
        public async Task Transfer_LayLaiTrang_DanhDauLai()
        {
            _executor.Next = new FormExecutionResult { Data = Invoices() };
            var (_, first, _) = await PostAsync("/api/invoice/transfer?unitCode=CTY", "{}");
            string cursor = first.GetProperty("paging").GetProperty("cursor").GetString()!;

            await PostAsync($"/api/invoice/transfer?unitCode=CTY&cursor={cursor}&page=1", "");
            await PostAsync($"/api/invoice/transfer?unitCode=CTY&cursor={cursor}&page=1", "");

            Assert.Equal([["HD0", "HD1"], ["HD2", "HD3"], ["HD2", "HD3"]], _executor.Marked.Select(m => m.Keys.ToArray()));
        }

        [Fact]
        public async Task Transfer_DanhDauLoi_KhongTraDuLieu()
        {
            _executor.Next = new FormExecutionResult { Data = Invoices() };
            _executor.MarkCode = "LOCKED";

            var (status, json, _) = await PostAsync("/api/invoice/transfer?unitCode=CTY", "{}");

            Assert.Equal(HttpStatusCode.InternalServerError, status);
            Assert.Equal("TRANSFER_MARK_FAILED", json.GetProperty("code").GetString());
            Assert.Contains("LOCKED", json.GetProperty("message").GetString());
            Assert.False(json.TryGetProperty("data", out _));
        }

        [Fact]
        public async Task KhongPhaiTransfer_KhongDanhDau()
        {
            _executor.Next = new FormExecutionResult { Data = Invoices() };

            var (_, json, _) = await PostAsync("/api/invoice/get?unitCode=CTY", "{}");

            Assert.Empty(_executor.Marked);
            Assert.False(json.GetProperty("paging").TryGetProperty("transferredCount", out _));
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

        [Fact]
        public async Task ThieuSigner_Unauthorized_KhongChayForm_RouteKhongCoVan404()
        {
            // Route lọt khỏi SignatureMiddleware (cấu hình pipeline sai) không được chạy form với signer null
            _withoutSigner = true;

            var (status, json, _) = await PostAsync("/api/customer/get", "{}");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("UNAUTHORIZED", json.GetProperty("code").GetString());
            Assert.Equal(0, _executor.Calls);

            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/khong-co", "{}")).Status);
        }

        private sealed class FakeExecutor : IFormExecutor
        {
            public FormExecutionResult Next { get; set; } = new() { Data = new DataSet() };
            public FormCallContext? LastCall { get; private set; }
            public MappedBody? LastBody { get; private set; }
            public int Calls { get; private set; }
            public bool Throw { get; set; }
            public List<(IReadOnlyList<string> Keys, string Unit)> Marked { get; } = [];
            public string MarkCode { get; set; } = "SUCCESS";

            public Task<string> MarkTransferredAsync(ImportForm form, IReadOnlyList<string> keys, FormCallContext call, CancellationToken cancellationToken = default)
            {
                Marked.Add((keys, call.Unit));
                return Task.FromResult(MarkCode);
            }

            public Task<FormExecutionResult> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default)
            {
                Calls++;
                LastCall = call;
                LastBody = body;
                if (Throw)
                {
                    throw new InvalidOperationException("lỗi SQL giả");
                }
                return Task.FromResult(Next);
            }
        }

        /// <summary>Giữ trong bộ nhớ, cùng quy tắc với IdempotencyStore</summary>
        private sealed class FakeIdempotency : IIdempotencyStore
        {
            private readonly Dictionary<string, (string Form, string Sha)> _reserved = new();
            public Dictionary<string, (int Status, string Body)> Saved { get; } = new();
            public List<string> Released { get; } = [];
            public IReadOnlyCollection<string> Reserved => _reserved.Keys;

            public void Pending(string key) => _reserved[key] = ("setSaleInvoice", "dang-chay");

            public Task<IdempotencyResult> ReserveAsync(int uId, string key, string form, string requestSha256, CancellationToken cancellationToken = default)
            {
                if (!_reserved.TryGetValue(key, out var existing))
                {
                    _reserved[key] = (form, requestSha256);
                    return Task.FromResult(new IdempotencyResult(IdempotencyState.Reserved));
                }
                if (existing.Sha == "dang-chay")
                {
                    return Task.FromResult(new IdempotencyResult(IdempotencyState.InProgress));
                }
                if (existing != (form, requestSha256))
                {
                    return Task.FromResult(new IdempotencyResult(IdempotencyState.Conflict));
                }
                var saved = Saved[key];
                return Task.FromResult(new IdempotencyResult(IdempotencyState.Replay, saved.Status, saved.Body));
            }

            public Task CompleteAsync(int uId, string key, int statusCode, string responseBody, CancellationToken cancellationToken = default)
            {
                Saved[key] = (statusCode, responseBody);
                return Task.CompletedTask;
            }

            public Task ReleaseAsync(int uId, string key)
            {
                _reserved.Remove(key);
                Released.Add(key);
                return Task.CompletedTask;
            }
        }

        /// <summary>CTY được phép, KHONGCO không tồn tại, còn lại bị từ chối</summary>
        private sealed class FakeUnitRights : IUnitRightsChecker
        {
            private readonly HashSet<string> _allowed = ["CTY"];
            public string? LastUnit { get; private set; }
            public string? LastAction { get; private set; }

            public void Allow(string unit) => _allowed.Add(unit);

            public Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default)
            {
                LastUnit = unitCode;
                LastAction = action;
                return Task.FromResult(_allowed.Contains(unitCode) ? "SUCCESS" : unitCode == "KHONGCO" ? "UNIT_NOT_FOUND" : "UNIT_DENIED");
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
                  <fields name="master" key="yourId"><field name="yourId" fastName="id_doitac" isPrimaryKey="true"/></fields>
                  <query><text>select 1</text></query>
                </import>
                """));
            var call = new FormCallContext(7, "DOITAC", 1, "ADMIN    ", true, "CT'Y", false);
            var config = new AppConfig { appDatabaseName = "FSD_A", sysDatabaseName = "FSD]S" };

            string sql = FormVariables.Replace(
                "exec p @@voucherCode, @@unit, @@userId, @@userName, @@admin, @@form, @@verb, @@resource, @@type; select * from @@sysDatabaseName..x, @@appDatabaseName..y; select @@primaryKeyListAlias; @@khongCo @@ROWCOUNT",
                form, call, config);

            Assert.Equal(
                "exec p N'HDA', N'CT''Y', 1, N'ADMIN', 1, N'setSaleInvoice', N'set', N'sale-invoice', N'Voucher'; select * from [FSD]]S]..x, [FSD_A]..y; select id_doitac AS yourId; @@khongCo @@ROWCOUNT",
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
            table.Rows.Add("master", 1, 1, "customerId", "CUSTOMER_NOT_FOUND");
            table.Rows.Add("detail", 2, 0, "itemId", "ITEM_NOT_FOUND");

            Assert.Equal("[1].master.customerId", FormExecutor.ToError(table.Rows[0], "master").Position);
            Assert.Equal("[0].detail[2].itemId", FormExecutor.ToError(table.Rows[1], "master").Position);
        }
    }
}
