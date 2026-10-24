using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Business;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Models;
using Models.Options;
using Provider;
using Security;

namespace Tests
{
    /// <summary>Đồng hồ cố định cho các lớp nhận TimeProvider</summary>
    internal sealed class FixedTime(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    public class MessageAuthenticatorTests
    {
        private const long Now = 1792574400;
        private static readonly byte[] PrivateKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"master\":{\"YourID\":\"HD001\"}}");

        private static readonly SignerKeys Signer = new(7, "DOITAC", true,
            MessageSignature.DeriveRequestKey(PrivateKey), MessageSignature.DeriveResponseKey(PrivateKey), 1, "ADMIN");

        private static MessageAuthenticator Create() =>
            new(new AppConfig { clockSkewSeconds = 300 }, new NonceStore(new MemoryCache(new MemoryCacheOptions())), new FixedTime(Now));

        private static string Sign(string nonce, byte[] body) => MessageSignature.HmacHex(Signer.RequestKey!, MessageSignature.RequestMessage(body, nonce));

        [Theory]
        [InlineData("", "abc", "MISSING_SIGNATURE")]
        [InlineData("1792574400-abcdefgh", "", "MISSING_SIGNATURE")]
        [InlineData("khongphaiso-abcdefgh", "abc", "MISSING_SIGNATURE")]
        [InlineData("1792574099-abcdefgh", "abc", "TIMESTAMP_EXPIRED")]
        [InlineData("1792574701-abcdefgh", "abc", "TIMESTAMP_EXPIRED")]
        [InlineData("1792574100-abcdefgh", "abc", null)]
        [InlineData("1792574700-abcdefgh", "abc", null)]
        public void CheckFreshness_NonceVaDoLechGio(string nonce, string signature, string? expected)
        {
            Assert.Equal(expected, Create().CheckFreshness(nonce, signature));
        }

        [Fact]
        public void VerifySignature_DungMotLan_LanHaiLaNonceReused()
        {
            var authenticator = Create();
            string nonce = $"{Now}-abcdefgh01";

            Assert.Null(authenticator.VerifySignature(Signer, Body, nonce, Sign(nonce, Body)));
            Assert.Equal("NONCE_REUSED", authenticator.VerifySignature(Signer, Body, nonce, Sign(nonce, Body)));
        }

        [Fact]
        public void VerifySignature_ChuKySai_KhongDotNonce()
        {
            var authenticator = Create();
            string nonce = $"{Now}-abcdefgh02";
            byte[] tampered = Encoding.UTF8.GetBytes("{\"master\":{\"YourID\":\"HD002\"}}");

            Assert.Equal("INVALID_SIGNATURE", authenticator.VerifySignature(Signer, tampered, nonce, Sign(nonce, Body)));
            // Request giả không làm hỏng nonce của request thật
            Assert.Null(authenticator.VerifySignature(Signer, Body, nonce, Sign(nonce, Body)));
        }

        [Fact]
        public void VerifySignature_KhongPhanBietHoaThuong()
        {
            string nonce = $"{Now}-abcdefgh03";
            Assert.Null(Create().VerifySignature(Signer, Body, nonce, Sign(nonce, Body).ToUpperInvariant()));
        }

        [Fact]
        public void SignResponse_TheoCanonicalVaKResponse()
        {
            byte[] response = Encoding.UTF8.GetBytes("{\"code\":\"SUCCESS\"}");

            ResponseSignature signature = Create().SignResponse(Signer, 200, "req-1", "1792574400-abcdefgh04", response);

            Assert.Equal(Now, signature.Timestamp);
            Assert.Equal(MessageSignature.Sha256Hex(response), signature.ContentSha256);
            string canonical = MessageSignature.CanonicalResponse(200, "req-1", "1792574400-abcdefgh04", Now, signature.ContentSha256);
            Assert.Equal(MessageSignature.HmacHex(Signer.ResponseKey!, canonical), signature.Signature);
            // Khóa request không ký được response
            Assert.NotEqual(MessageSignature.HmacHex(Signer.RequestKey!, canonical), signature.Signature);
        }
    }

    public class AccessTokenIssuerTests
    {
        private const string SecretKey = "khoa-ky-jwt-chi-dung-cho-test-0123456789";
        private static readonly JwtTokenService Jwt = new(SecretKey, 60, "FastRestApi");
        private static readonly SignerKeys Signer = new(7, "DOITAC", true, null, null, 12, "ADMIN");

        private static AccessTokenIssuer Create() =>
            new(Jwt, new AppConfig { tokenMinutes = 60, portalTokenMinutes = 30 }, TimeProvider.System);

        [Fact]
        public void IssueApiToken_ClaimV3_ScopeApi_HanTheoTokenMinutes()
        {
            IssuedToken token = Create().IssueApiToken(Signer);

            Assert.Equal(3600, token.ExpiresIn);
            ClaimsPrincipal principal = Jwt.ValidateJWTToken(token.AccessToken, "khong-dung-ip")!;
            Assert.NotNull(principal);
            Assert.Equal("7", principal.FindFirst("u_id")?.Value);
            Assert.Equal("12", principal.FindFirst("user_id")?.Value);
            Assert.Equal("ADMIN", principal.FindFirst("name_erp")?.Value);
            Assert.Equal("api", principal.FindFirst("scope")?.Value);

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
            Assert.Equal([JwtTokenService.Audience], jwt.Audiences);
            Assert.InRange(jwt.ValidTo, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
        }

        [Fact]
        public void IssuePortalToken_ScopePortal_HanTheoPortalTokenMinutes()
        {
            IssuedToken token = Create().IssuePortalToken(Signer);

            Assert.Equal(1800, token.ExpiresIn);
            Assert.Equal("portal", Jwt.ValidateJWTToken(token.AccessToken, "")!.FindFirst("scope")?.Value);
        }

        [Fact]
        public void MoiLanCap_JtiKhacNhau()
        {
            var issuer = Create();
            string Jti(IssuedToken token) => new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken).Id;

            Assert.NotEqual(Jti(issuer.IssueApiToken(Signer)), Jti(issuer.IssueApiToken(Signer)));
        }
    }

    public class FormResultSetTests
    {
        private static DataTable Head(string? code, string? message = null, string? tables = null)
        {
            DataTable head = new("Table");
            List<object?> values = [];
            if (code != null) { head.Columns.Add("code"); values.Add(code); }
            if (message != null) { head.Columns.Add("message"); values.Add(message); }
            if (tables != null) { head.Columns.Add("tables"); values.Add(tables); }
            head.Rows.Add(values.ToArray());
            return head;
        }

        private static DataSet Data(DataTable head, int dataTables)
        {
            DataSet data = new();
            data.Tables.Add(head);
            for (int i = 0; i < dataTables; i++)
            {
                DataTable table = new($"Table{i + 1}");
                table.Columns.Add("id");
                table.Rows.Add(i);
                data.Tables.Add(table);
            }
            return data;
        }

        [Fact]
        public void KhongCoBang_LaSuccessRong()
        {
            FormResultSet result = FormResultSet.From(new DataSet());

            Assert.True(result.IsSuccess);
            Assert.Equal("", result.Message);
            Assert.Empty(result.Tables);
        }

        [Fact]
        public void BangDauKhongCoCotCode_LaSuccess()
        {
            Assert.Equal("SUCCESS", FormResultSet.From(Data(Head(null, message: "ok"), 0)).Code);
        }

        [Fact]
        public void MotBangDuLieu_TenData()
        {
            FormResultSet result = FormResultSet.From(Data(Head(" SUCCESS ", "xong"), 1));

            Assert.Equal("SUCCESS", result.Code);
            Assert.Equal("xong", result.Message);
            Assert.Equal(["data"], result.Tables.Keys);
            Assert.Equal(1, result.RowCount);
        }

        [Fact]
        public void NhieuBang_TenTheoTables_ThieuThiDataN()
        {
            FormResultSet result = FormResultSet.From(Data(Head("SUCCESS", tables: "master, detail"), 3));

            Assert.Equal(["master", "detail", "data3"], result.Tables.Keys);
            Assert.Equal(3, result.RowCount);
        }

        [Fact]
        public void CodeNull_LaChuoiRong_KhongPhaiSuccess()
        {
            DataTable head = new();
            head.Columns.Add("code");
            head.Rows.Add(DBNull.Value);

            Assert.Equal("", FormResultSet.CodeOf(Data(head, 0)));
        }
    }

    public class FormRequestServiceTests
    {
        private static readonly ImportForm GetCustomer = ColumnTypeResolver.Resolve(ImportFormLoader.Parse("getCustomer", XDocument.Parse("""
            <import xmlns="urn:schemas-fast-com:fast-api" type="List">
              <fields name="filter"><field name="Keyword" fastName="tu_khoa"/></fields>
              <query><text>select 1</text></query>
            </import>
            """)), _ => null);

        private static readonly ImportForm SaleInvoice = ColumnTypeResolver.Resolve(ImportFormLoader.Parse("setSaleInvoice", XDocument.Parse("""
            <import xmlns="urn:schemas-fast-com:fast-api" type="Voucher" unitRequired="true">
              <fields name="master" key="YourID"><field name="YourID" fastName="id_doitac"/></fields>
              <query><text>select 1</text></query>
            </import>
            """)), _ => null);

        private static readonly SignerKeys Signer = new(7, "DOITAC", true, null, null, 1, "ADMIN")
        {
            AllowForms = new HashSet<string>(["getCustomer"], StringComparer.OrdinalIgnoreCase)
        };

        private sealed class StubExecutor(DataSet data) : IFormExecutor
        {
            public Task<FormExecutionResult> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default) =>
                Task.FromResult(new FormExecutionResult { Data = data });

            public Task<string> MarkTransferredAsync(ImportForm form, IReadOnlyList<string> keys, FormCallContext call, CancellationToken cancellationToken = default) =>
                Task.FromResult("SUCCESS");
        }

        private sealed class DenyUnits : IUnitRightsChecker
        {
            public Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default) =>
                Task.FromResult(unitCode == "CTY" ? "SUCCESS" : "UNIT_DENIED");
        }

        private static FormRequestService Create(DataSet data, int maxRows = 50)
        {
            var services = new ServiceCollection();
            services.AddHybridCache();
            var config = new AppConfig { maxRowsWithoutPaging = maxRows, maxDocumentsPerRequest = 5, cursorTtlMinutes = 5 };
            var options = new Options();
            options.Messages.Add(new Message { code = "SUCCESS", success = true, httpStatusCode = 200, message = "Thành công" });
            options.Messages.Add(new Message { code = "CUSTOMER_NOT_FOUND", success = false, httpStatusCode = 404, message = "Không có khách hàng" });
            return new FormRequestService(new StubExecutor(data), new DenyUnits(),
                new CursorStore(config, services.BuildServiceProvider().GetRequiredService<HybridCache>()), options, config);
        }

        private static DataSet Rows(string code, int rows, string message = "")
        {
            DataSet data = new();
            DataTable head = new("Table");
            head.Columns.Add("code");
            head.Columns.Add("message");
            head.Rows.Add(code, message);
            data.Tables.Add(head);
            DataTable table = new("Table1");
            table.Columns.Add("ma_kh");
            for (int i = 0; i < rows; i++)
            {
                table.Rows.Add($"KH{i}");
            }
            data.Tables.Add(table);
            return data;
        }

        private static FormCallContext Call => FormRequestService.CallContext(Signer, "", false);

        private static MappedBody Body(FormRequestService service) => service.MapBody(GetCustomer, "{}"u8.ToArray(), null).Body;

        [Fact]
        public async Task Authorize_KhongCoQuyenForm_Forbidden()
        {
            Assert.Equal(new FormFailed("FORBIDDEN"), await Create(new DataSet()).AuthorizeAsync(SaleInvoice, Signer, "CTY"));
        }

        [Fact]
        public async Task Authorize_DonViSai_TraMaCuaKiemDonVi()
        {
            var service = Create(new DataSet());

            Assert.Null(await service.AuthorizeAsync(GetCustomer, Signer, ""));
            Assert.Null(await service.AuthorizeAsync(GetCustomer, Signer, "CTY"));
            Assert.Equal(new FormFailed("UNIT_DENIED"), await service.AuthorizeAsync(GetCustomer, Signer, "KHAC"));
        }

        [Fact]
        public async Task Authorize_FormBatBuocDonVi_ThieuUnit()
        {
            SignerKeys signer = Signer with { AllowForms = new HashSet<string>(["setSaleInvoice"], StringComparer.OrdinalIgnoreCase) };
            Assert.Equal(new FormFailed("UNIT_REQUIRED"), await Create(new DataSet()).AuthorizeAsync(SaleInvoice, signer, ""));
        }

        [Fact]
        public void MapBody_SaiDang_InvalidShape()
        {
            var (_, rejected) = Create(new DataSet()).MapBody(GetCustomer, "[1,2]"u8.ToArray(), null);
            Assert.Equal("INVALID_SHAPE", Assert.IsType<FormFailed>(rejected).Code);
        }

        [Fact]
        public async Task Execute_VuotMaxRowsKhongPaging_ResultTooLarge()
        {
            var service = Create(Rows("SUCCESS", 3), maxRows: 2);

            FormFailed failed = Assert.IsType<FormFailed>(await service.ExecuteAsync(GetCustomer, Body(service), Call));

            Assert.Equal("RESULT_TOO_LARGE", failed.Code);
            Assert.Equal("3 dòng vượt 2: khai <paging> cho form getCustomer", failed.Message);
        }

        [Fact]
        public async Task Execute_MessageRong_LayMessageCuaMa()
        {
            var service = Create(Rows("CUSTOMER_NOT_FOUND", 1));

            FormCompleted completed = Assert.IsType<FormCompleted>(await service.ExecuteAsync(GetCustomer, Body(service), Call));

            Assert.Equal("CUSTOMER_NOT_FOUND", completed.Code);
            Assert.Equal("Không có khách hàng", completed.Message);
            Assert.Equal(["data"], completed.Tables.Keys);
        }

        [Fact]
        public async Task Execute_MaLoiKhongKiemSoDong()
        {
            // Giới hạn dòng chỉ áp cho SUCCESS: dữ liệu kèm mã lỗi vẫn trả về
            var service = Create(Rows("CUSTOMER_NOT_FOUND", 10, "chi tiết"), maxRows: 2);

            FormCompleted completed = Assert.IsType<FormCompleted>(await service.ExecuteAsync(GetCustomer, Body(service), Call));
            Assert.Equal("chi tiết", completed.Message);
        }

        [Fact]
        public async Task NextPage_CursorKhongCo_CursorExpired()
        {
            Assert.Equal(new FormFailed("CURSOR_EXPIRED"), await Create(new DataSet()).NextPageAsync(GetCustomer, Signer, "", "khongco", "0"));
        }
    }

    public class UnitRightsCheckerTests
    {
        private sealed class CountingRepository : IUnitRightsRepository
        {
            public int Calls { get; private set; }

            public Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult("SUCCESS");
            }
        }

        private static (UnitRightsChecker Checker, CountingRepository Repository) Create()
        {
            var services = new ServiceCollection();
            services.AddHybridCache();
            var repository = new CountingRepository();
            return (new UnitRightsChecker(new AppConfig { userCacheSeconds = 60 }, services.BuildServiceProvider().GetRequiredService<HybridCache>(), repository), repository);
        }

        [Fact]
        public async Task MaDaiHon8KyTu_KhongGoiSql()
        {
            var (checker, repository) = Create();

            Assert.Equal("UNIT_NOT_FOUND", await checker.CheckAsync(1, "CTY123456", "New"));
            Assert.Equal(0, repository.Calls);
        }

        [Fact]
        public async Task KetQuaCacheTheoUserDonViThaoTac_KhongPhanBietHoaThuongDonVi()
        {
            var (checker, repository) = Create();

            await checker.CheckAsync(1, "cty", "New");
            await checker.CheckAsync(1, "CTY", "New");
            Assert.Equal(1, repository.Calls);

            await checker.CheckAsync(1, "CTY", "Edit");
            await checker.CheckAsync(2, "CTY", "New");
            Assert.Equal(3, repository.Calls);
        }
    }

    public class SignerKeyStoreTests
    {
        private static readonly string Kek = PrivateKeyProtector.GenerateKeyEncryptionKey();
        private static readonly byte[] PrivateKey = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();

        private sealed class Users(params UserRights[] users) : IUserRightsRepository
        {
            public Task<UserRights?> GetUserRightsAsync(string userName, CancellationToken cancellationToken = default) =>
                Task.FromResult(users.FirstOrDefault(u => string.Equals(u.Name, userName, StringComparison.OrdinalIgnoreCase)));
        }

        private static SignerKeyStore Create(params UserRights[] users)
        {
            var services = new ServiceCollection();
            services.AddHybridCache();
            return new SignerKeyStore(new AppConfig { userCacheSeconds = 60 }, services.BuildServiceProvider().GetRequiredService<HybridCache>(),
                new PrivateKeyProtector(Kek), new Users(users), NullLogger<SignerKeyStore>.Instance);
        }

        private static UserRights Rights(int uId, string name, string? privateKey) => new()
        {
            UId = uId, Name = name, NameErp = "ADMIN", Status = '1', ErpStatus = '1', UserId = 1, ErpAdmin = true, PrivateKey = privateKey,
            AllowForms = new HashSet<string>(["getCustomer"], StringComparer.OrdinalIgnoreCase)
        };

        [Fact]
        public async Task GiaiPrivateKey_RaKhoaRequestVaResponse()
        {
            string stored = new PrivateKeyProtector(Kek).Protect(PrivateKey, 7);

            SignerKeys keys = (await Create(Rights(7, "DOITAC", stored)).GetAsync("DOITAC"))!;

            Assert.True(keys.HasKey);
            Assert.Equal(MessageSignature.DeriveRequestKey(PrivateKey), keys.RequestKey);
            Assert.Equal(MessageSignature.DeriveResponseKey(PrivateKey), keys.ResponseKey);
            Assert.True(keys.IsActive);
            Assert.True(keys.ErpAdmin);
            Assert.True(keys.CanAccessForm("GETCUSTOMER"));
        }

        [Fact]
        public async Task BanMaCuaUserKhac_CoiNhuChuaCapKhoa()
        {
            // private_key gắn u_id: chép bản mã của u_id 7 sang u_id 8 không giải được
            string stored = new PrivateKeyProtector(Kek).Protect(PrivateKey, 7);

            SignerKeys keys = (await Create(Rights(8, "KHAC", stored)).GetAsync("KHAC"))!;

            Assert.False(keys.HasKey);
        }

        [Fact]
        public async Task KhongCoTaiKhoan_Null()
        {
            Assert.Null(await Create().GetAsync("KHONGCO"));
        }
    }

    public class SqlDatabaseTests
    {
        [Theory]
        [InlineData(0, SqlDatabase.DefaultCommandTimeout)]
        [InlineData(-5, SqlDatabase.DefaultCommandTimeout)]
        [InlineData(360, 360)]
        public void CommandTimeout_KhongDuongThiMacDinh(int configured, int expected)
        {
            Assert.Equal(expected, new SqlDatabase("Server=.;Database=x", configured).CommandTimeout);
        }
    }
}
