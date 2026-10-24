using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Business;
using Models;

namespace Tests
{
    public class ColumnTypeResolverTests
    {
        private static readonly Dictionary<string, ColumnInfo> M81 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ma_kh"] = new("ma_kh", "char", 16, 0, 0),
            ["ngay_ct"] = new("ngay_ct", "smalldatetime", 4, 16, 0),
            ["ong_ba"] = new("ong_ba", "nvarchar", 128, 0, 0),
            ["t_tt_nt"] = new("t_tt_nt", "numeric", 9, 19, 4),
            ["dien_giai"] = new("dien_giai", "nvarchar", -1, 0, 0),
            ["km_yn"] = new("km_yn", "tinyint", 1, 3, 0)
        };

        private static ImportForm Form(string fields, string table = "table=\"m81$000000\"") => ImportFormLoader.Parse("setCustomer", XDocument.Parse($"""
            <import xmlns="urn:schemas-fast-com:fast-api" type="List">
              <fields name="master" {table}>{fields}</fields>
              <query><text>select 1</text></query>
            </import>
            """));

        private static ImportForm Resolve(ImportForm form) => ColumnTypeResolver.Resolve(form, table => table == "m81$000000" ? M81 : null);

        [Theory]
        [InlineData("ma_kh", "string", "CHAR(16) COLLATE DATABASE_DEFAULT", 16)]
        [InlineData("ngay_ct", "datetime", "SMALLDATETIME", null)]
        [InlineData("ong_ba", "string", "NVARCHAR(128) COLLATE DATABASE_DEFAULT", 128)]
        [InlineData("t_tt_nt", "numeric", "NUMERIC(19,4)", null)]
        [InlineData("dien_giai", "string", "NVARCHAR(MAX) COLLATE DATABASE_DEFAULT", null)]
        [InlineData("km_yn", "int", "TINYINT", null)]
        public void CoTable_TheoCotThat(string column, string type, string sqlType, int? maxLength)
        {
            ImportField field = Resolve(Form($"<field name=\"a\" fastName=\"{column}\" type=\"{type}\"/>")).Root.Fields[0];

            Assert.Equal(sqlType, field.SqlType);
            Assert.Equal(maxLength, field.EffectiveMaxLength);
        }

        [Fact]
        public void MaxLengthLonHonCot_CanhBao_KiemTheoCot()
        {
            ImportForm form = Resolve(Form("<field name=\"a\" fastName=\"ma_kh\" maxLength=\"36\"/><field name=\"b\" fastName=\"ong_ba\" maxLength=\"50\"/>"));

            Assert.Equal(16, form.Root.Fields[0].EffectiveMaxLength);
            Assert.Equal(50, form.Root.Fields[1].EffectiveMaxLength);
            Assert.Single(form.Warnings, w => w.Contains("maxLength 36"));
        }

        [Fact]
        public void CotKhongCoTrongBang_DungTypeXml_CanhBao()
        {
            ImportForm form = Resolve(Form("<field name=\"a\" fastName=\"id_doitac\" maxLength=\"36\"/>"));

            Assert.Equal("NVARCHAR(36) COLLATE DATABASE_DEFAULT", form.Root.Fields[0].SqlType);
            Assert.Contains(form.Warnings, w => w.Contains("không có cột id_doitac"));
        }

        [Theory]
        [InlineData("string", "", "NVARCHAR(MAX) COLLATE DATABASE_DEFAULT")]
        [InlineData("string", "maxLength=\"20\"", "NVARCHAR(20) COLLATE DATABASE_DEFAULT")]
        [InlineData("numeric", "", "NUMERIC(30,12)")]
        [InlineData("int", "", "INT")]
        [InlineData("datetime", "", "DATETIME")]
        [InlineData("unixtime", "", "DATETIME")]
        [InlineData("bool", "", "BIT")]
        public void KhongTable_TheoTypeXml(string type, string extra, string sqlType)
        {
            ImportForm form = Resolve(Form($"<field name=\"a\" fastName=\"a\" type=\"{type}\" {extra}/>", table: ""));

            Assert.Equal(sqlType, form.Root.Fields[0].SqlType);
            Assert.Empty(form.Warnings);
        }

        [Fact]
        public void BangKhongTonTai_LoiLucNap()
        {
            var ex = Assert.Throws<ImportFormException>(() => Resolve(Form("<field name=\"a\" fastName=\"a\"/>", "table=\"khong_co\"")));
            Assert.Contains("không tồn tại", ex.Message);
        }
    }

    public class JsonBodyMapperTests
    {
        private const string Xml = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="Voucher">
              <fields name="master" key="yourId">
                <field name="yourId" fastName="id_doitac" maxLength="10" allowNull="false"/>
                <field name="voucherDate" fastName="ngay_ct" type="datetime" format="dd/MM/yyyy" allowNull="false"/>
                <field name="customerId" fastName="ma_kh" upperCase="true" maxLength="8"/>
                <field name="note" fastName="dien_giai" checkIrregular="true"/>
                <field name="status" fastName="status" type="int" defaultValue="1"/>
                <field name="computed" fastName="ma_dvcs" hidden="true" allowNull="false"/>
              </fields>
              <fields name="detail">
                <field name="itemId" fastName="ma_vt" allowNull="false"/>
                <field name="quantity" fastName="so_luong" type="numeric"/>
                <field name="promotion" fastName="km_yn" type="bool"/>
                <field name="createdAt" fastName="datetime0" type="unixtime"/>
              </fields>
              <query><text>select 1</text></query>
            </import>
            """;

        private static readonly ImportForm Form = ColumnTypeResolver.Resolve(ImportFormLoader.Parse("setSaleInvoice", XDocument.Parse(Xml)), _ => null);
        private static readonly Regex Irregular = new("['`~!@#$%^&*(),;\"<>?/\\\\,_]");

        private static MappedBody Map(string json, int maxDocuments = 10) => JsonBodyMapper.Map(Form, Encoding.UTF8.GetBytes(json), maxDocuments, Irregular);

        [Fact]
        public void ChungTuDung_RaBangTheoKhoi()
        {
            MappedBody body = Map("""
                { "master": { "yourId": "HD001", "voucherDate": "21/10/2026", "customerId": "kh01", "Unknown": 1 },
                  "detail": [ { "itemId": "VT01", "quantity": 2.5, "promotion": true, "createdAt": 1792574400 },
                              { "itemId": "VT02", "quantity": "1" } ] }
                """);

            Assert.Null(body.ShapeError);
            Assert.Empty(body.Errors);
            Assert.Equal(1, body.DocumentCount);

            DataRow master = body.Data.Tables["master"]!.Rows[0];
            Assert.Equal(0, master["_row"]);
            Assert.Equal("HD001", master["id_doitac"]);
            Assert.Equal(new DateTime(2026, 10, 21), master["ngay_ct"]);
            Assert.Equal("KH01", master["ma_kh"]);
            Assert.Equal(1, master["status"]);
            Assert.Equal(DBNull.Value, master["ma_dvcs"]);

            DataTable detail = body.Data.Tables["detail"]!;
            Assert.Equal(2, detail.Rows.Count);
            Assert.Equal("HD001", detail.Rows[0]["id_doitac"]);
            Assert.Equal(2.5m, detail.Rows[0]["so_luong"]);
            Assert.Equal(true, detail.Rows[0]["km_yn"]);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792574400).LocalDateTime, detail.Rows[0]["datetime0"]);
            Assert.Equal("VT02", detail.Rows[1]["ma_vt"]);
            Assert.Equal(1, detail.Rows[1]["_row"]);
            Assert.Equal(0, detail.Rows[1]["_parent"]);
        }

        [Fact]
        public void MangChungTu_ParentTheoChiSo()
        {
            MappedBody body = Map("""
                [ { "master": { "yourId": "a", "voucherDate": "2026-10-21" } },
                  { "master": { "yourId": "b", "voucherDate": "2026-10-22T08:30:00" }, "detail": [ { "itemId": "X" } ] } ]
                """);

            Assert.Empty(body.Errors);
            Assert.Equal(2, body.DocumentCount);
            Assert.Equal(1, body.Data.Tables["master"]!.Rows[1]["_row"]);
            Assert.Equal(new DateTime(2026, 10, 22, 8, 30, 0), body.Data.Tables["master"]!.Rows[1]["ngay_ct"]);
            Assert.Equal(1, body.Data.Tables["detail"]!.Rows[0]["_parent"]);
            Assert.Equal("b", body.Data.Tables["detail"]!.Rows[0]["id_doitac"]);
        }

        [Fact]
        public void LoiGiaTri_GomDu_CoViTri()
        {
            MappedBody body = Map("""
                [ { "master": { "yourId": "", "voucherDate": "31/02/2026", "customerId": "QUA_DAI_123", "note": "a<b", "status": "x" },
                    "detail": [ { "quantity": 1 }, { "itemId": "VT", "quantity": "abc", "promotion": 2, "createdAt": "x" } ] } ]
                """);

            Assert.Null(body.ShapeError);
            Assert.Equal(
            [
                ("[0].master.yourId", "FIELD_REQUIRED"),
                ("[0].master.voucherDate", "INVALID_VALUE"),
                ("[0].master.customerId", "VALUE_TOO_LONG"),
                ("[0].master.note", "INVALID_FIELD_IRREGULAR"),
                ("[0].master.status", "INVALID_VALUE"),
                ("[0].detail[0].itemId", "FIELD_REQUIRED"),
                ("[0].detail[1].quantity", "INVALID_VALUE"),
                ("[0].detail[1].promotion", "INVALID_VALUE"),
                ("[0].detail[1].createdAt", "INVALID_VALUE")
            ], body.Errors.Select(e => (e.Position, e.Code)));
        }

        [Fact]
        public void FieldHidden_KhongNhanTuJson_KhongBatBuoc()
        {
            MappedBody body = Map("""{ "master": { "yourId": "a", "voucherDate": "21/10/2026", "computed": "CTY" } }""");

            Assert.Empty(body.Errors);
            Assert.Equal(DBNull.Value, body.Data.Tables["master"]!.Rows[0]["ma_dvcs"]);
        }

        [Theory]
        [InlineData("", "thiếu khối gốc")]
        [InlineData("123", "object hoặc mảng")]
        [InlineData("[]", "rỗng")]
        [InlineData("{ \"yourId\": \"a\" }", "thiếu khối gốc 'master'")]
        [InlineData("{ \"master\": { \"yourId\": \"a\" }, \"detail\": {} }", "phải là mảng")]
        [InlineData("{ \"master\": { \"yourId\": \"a\" }, \"detail\": [1] }", "phải là object")]
        [InlineData("{ \"master\": { \"yourId\": \"a\" }, \"detail\": [ { \"yourId\": \"b\" } ] }", "khác khóa khối gốc")]
        [InlineData("[ { \"master\": { \"yourId\": \"a\" } }, 5 ]", "[1]")]
        [InlineData("{ \"master\": ", "JSON không hợp lệ")]
        public void SaiDang_InvalidShape(string json, string message)
        {
            MappedBody body = Map(json);

            Assert.NotNull(body.ShapeError);
            Assert.Contains(message, body.ShapeError);
            Assert.Equal(0, body.Data.Tables["master"]!.Rows.Count);
        }

        [Fact]
        public void QuaSoChungTu_InvalidShape()
        {
            MappedBody body = Map("[ { \"master\": { \"yourId\": \"a\" } }, { \"master\": { \"yourId\": \"b\" } } ]", maxDocuments: 1);

            Assert.Contains("tối đa 1", body.ShapeError);
        }

        [Fact]
        public void KhoiConGuiKemKhoaTrung_ChapNhan()
        {
            MappedBody body = Map("""{ "master": { "yourId": "a", "voucherDate": "21/10/2026" }, "detail": [ { "yourId": "a", "itemId": "X" } ] }""");

            Assert.Null(body.ShapeError);
            Assert.Empty(body.Errors);
        }

        [Fact]
        public void FormMotKhoi_NhanDangPhang()
        {
            ImportForm form = ColumnTypeResolver.Resolve(ImportFormLoader.Parse("getCustomer", XDocument.Parse("""
                <import xmlns="urn:schemas-fast-com:fast-api" type="List">
                  <fields name="filter">
                    <field name="fromDate" fastName="ngay_tu" type="datetime" format="dd/MM/yyyy"/>
                    <field name="toDate" fastName="ngay_den" type="datetime" format="dd/MM/yyyy"/>
                  </fields>
                  <query><text>select 1</text></query>
                </import>
                """)), _ => null);

            foreach (string json in new[] { """{ "fromDate": "01/10/2026", "toDate": "31/10/2026" }""", """{ "filter": { "fromDate": "01/10/2026", "toDate": "31/10/2026" } }""" })
            {
                MappedBody body = JsonBodyMapper.Map(form, Encoding.UTF8.GetBytes(json), 10);
                Assert.Null(body.ShapeError);
                Assert.Equal(new DateTime(2026, 10, 31), body.Data.Tables["filter"]!.Rows[0]["ngay_den"]);
            }
        }

        [Fact]
        public void CreateTempTableSql_TheoSqlType()
        {
            Assert.Equal(
                "CREATE TABLE #detail ([_row] INT NOT NULL, [_parent] INT NOT NULL, [id_doitac] NVARCHAR(10) COLLATE DATABASE_DEFAULT NULL, [ma_vt] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL, "
                + "[so_luong] NUMERIC(30,12) NULL, [km_yn] BIT NULL, [datetime0] DATETIME NULL)",
                JsonBodyMapper.CreateTempTableSql(Form.Blocks[1]));
        }

        [Theory]
        [InlineData("21/10/2026", "dd/MM/yyyy", true)]
        [InlineData("2026-10-21", "dd/MM/yyyy", true)]
        [InlineData("2026-10-21T07:00:00Z", "", true)]
        [InlineData("10/21/2026", "dd/MM/yyyy", false)]
        [InlineData("21/10/2026", "", false)]
        public void TryParseDate_FormatHoacIso(string text, string format, bool ok)
        {
            Assert.Equal(ok, JsonBodyMapper.TryParseDate(text, format, out _));
        }
    }
}
