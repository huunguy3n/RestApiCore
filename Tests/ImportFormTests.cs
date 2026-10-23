using System.Xml.Linq;
using Business.V3;
using Models.V3;

namespace Tests
{
    public class ImportFormTests
    {
        /// <summary>Ví dụ mục 2.1, entity And khai "&#38;#13;" để CR không bị chuẩn hóa thành dấu cách</summary>
        private const string SaleInvoice = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE import [
              <!ENTITY And "&#38;#13;">
            ]>
            <import xmlns="urn:schemas-fast-com:fast-api" type="Voucher" voucherCode="HDA" unitRequired="true" description="Hóa đơn bán hàng">
              <fields name="master" table="m81$000000" key="YourID">
                <field name="YourID" fastName="id_doitac" type="string" maxLength="36" allowNull="false" isPrimaryKey="true"/>
                <field name="VoucherDate" fastName="ngay_ct" type="datetime" format="dd/MM/yyyy" allowNull="false"/>
                <field name="CustomerID" fastName="ma_kh" allowNull="false" upperCase="true" category="1"
                       check="ma_kh not in (select ma_kh from dmkh where status = '1')" errorCode="CUSTOMER_NOT_FOUND"/>
              </fields>
              <fields name="detail" table="d81$000000">
                <field name="ItemID" fastName="ma_vt" allowNull="false" upperCase="true" category="2"
                       check="ma_vt not in (select ma_vt from dmvt where status = '1')" errorCode="ITEM_NOT_FOUND"/>
                <field name="Promotion" fastName="km_yn" type="int" defaultValue="0" category="2"
                       check="km_yn not in (0, 1, 2, 3)&And;km_yn = 3 and exists (select 1 from @@table b where b.km_yn = 0)"
                       condition="&And;IF EXISTS (SELECT 1 FROM #master) BEGIN"
                       tag="&And;END"
                       errorCode="INVALID_FIELD&And;PROMOTION_MIXED"/>
                <field name="Quantity" fastName="so_luong" type="numeric"/>
              </fields>
              <query action="checking"><text><![CDATA[ @@checking(1) @@checking(2) ]]></text></query>
              <query action="processing"><text><![CDATA[ exec restApi_ProcessDataFor_m81 @@voucherCode, @@unit, @@userId ]]></text></query>
            </import>
            """;

        private static ImportForm Parse(string name, string xml) => ImportFormLoader.Parse(name, XDocument.Parse(xml.Trim()));

        private static string Minimal(string fields, string extra = "", string rootAttributes = "type=\"List\"") => $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE import [ <!ENTITY And "&#38;#13;"> ]>
            <import xmlns="urn:schemas-fast-com:fast-api" {{rootAttributes}}>
              {{fields}}
              {{extra}}
              <query action="processing"><text>select 'SUCCESS' as code</text></query>
            </import>
            """;

        [Theory]
        [InlineData("setSaleInvoice", "set", "sale-invoice", "/api/v3/sale-invoice")]
        [InlineData("getCustomer", "get", "customer", "/api/v3/customer/get")]
        [InlineData("cancelInvoice", "cancel", "invoice", "/api/v3/invoice/cancel")]
        [InlineData("approveInvoice", "approve", "invoice", "/api/v3/invoice/approve")]
        [InlineData("deleteSaleInvoice", "delete", "sale-invoice", "/api/v3/sale-invoice/delete")]
        [InlineData("getVATInvoice", "get", "vat-invoice", "/api/v3/vat-invoice/get")]
        [InlineData("getInvoice2", "get", "invoice2", "/api/v3/invoice2/get")]
        [InlineData("setCustomers", "set", "customers", "/api/v3/customers")]
        public void TachTenFile_RaDongTuVaRoute(string name, string verb, string resource, string route)
        {
            Assert.True(ImportFormLoader.TrySplitName(name, out string actualVerb, out string actualResource));
            Assert.Equal(verb, actualVerb);
            Assert.Equal(resource, actualResource);
            Assert.Equal(route, ImportFormLoader.RouteOf(actualVerb, actualResource));
        }

        [Theory]
        [InlineData("SetCustomer")]
        [InlineData("customer")]
        [InlineData("set_Customer")]
        [InlineData("set-customer")]
        [InlineData("")]
        public void TenFileSaiDang_KhongTachDuoc(string name)
        {
            Assert.False(ImportFormLoader.TrySplitName(name, out _, out _));
        }

        [Theory]
        [InlineData("get", "Acc")]
        [InlineData("set", "New")]
        [InlineData("delete", "Del")]
        [InlineData("cancel", "Edit")]
        public void UnitAction_SuyTuDongTu(string verb, string action)
        {
            Assert.Equal(action, ImportFormLoader.DefaultUnitAction(verb));
        }

        [Fact]
        public void DocViDu21_DungCauTruc()
        {
            ImportForm form = Parse("setSaleInvoice", SaleInvoice);

            Assert.Equal(ImportFormType.Voucher, form.Type);
            Assert.Equal("HDA", form.VoucherCode);
            Assert.True(form.UnitRequired);
            Assert.Equal("New", form.UnitAction);
            Assert.Equal("/api/v3/sale-invoice", form.Route);
            Assert.Equal(["master", "detail"], form.Blocks.Select(b => b.Name));
            Assert.True(form.Root.IsRoot);
            Assert.Equal("YourID", form.Root.Key);

            ImportField date = form.Root.Fields.Single(f => f.Name == "VoucherDate");
            Assert.Equal(ImportFieldType.DateTime, date.Type);
            Assert.Equal("dd/MM/yyyy", date.Format);
            Assert.False(date.AllowNull);

            ImportField promotion = form.Blocks[1].Fields.Single(f => f.Name == "Promotion");
            Assert.Equal(2, promotion.Checks.Count);
            Assert.Equal(new ImportCheck("km_yn not in (0, 1, 2, 3)", "", "", "INVALID_FIELD"), promotion.Checks[0]);
            Assert.Equal("IF EXISTS (SELECT 1 FROM #master) BEGIN", promotion.Checks[1].Condition);
            Assert.Equal("END", promotion.Checks[1].Tag);
            Assert.Equal("PROMOTION_MIXED", promotion.Checks[1].ErrorCode);
            Assert.Empty(form.Warnings);
        }

        [Fact]
        public void KhoiCon_TuThemKhoaKhoiGoc()
        {
            ImportForm form = Parse("setSaleInvoice", SaleInvoice);

            ImportField key = form.Blocks[1].Fields[0];
            Assert.Equal("YourID", key.Name);
            Assert.Equal("id_doitac", key.FastName);
            Assert.Equal(36, key.MaxLength);
            Assert.True(key.IsInheritedKey);
        }

        [Fact]
        public void CheckingTheoCategory_KhongTheoThuTu()
        {
            ImportForm form = Parse("setSaleInvoice", SaleInvoice);
            string sql = form.CheckingQuery;

            int customer = sql.IndexOf("'CUSTOMER_NOT_FOUND'");
            int item = sql.IndexOf("'ITEM_NOT_FOUND'");
            Assert.True(customer >= 0 && item > customer, "@@checking(1) chỉ có ma_kh, @@checking(2) có ma_vt, km_yn");
            Assert.Contains("SELECT 'master', _row, _parent, 'CustomerID', 'CUSTOMER_NOT_FOUND' FROM #master WHERE ISNULL(ma_kh, '') <> '' AND (ma_kh not in (select ma_kh from dmkh where status = '1'))", sql);
            Assert.Contains("FROM #detail WHERE km_yn IS NOT NULL AND (km_yn not in (0, 1, 2, 3))", sql);
            Assert.Contains("exists (select 1 from #detail b where b.km_yn = 0)", sql);
            Assert.Contains("IF EXISTS (SELECT 1 FROM #master) BEGIN", sql);
            Assert.DoesNotContain("@@checking", sql);
            Assert.DoesNotContain("@@table", sql);
        }

        [Fact]
        public void CheckingKhongSo_ChayMoiField()
        {
            ImportForm form = Parse("setCustomer", Minimal("""
                <fields name="master">
                  <field name="A" fastName="a" check="a = 1" errorCode="E1" category="5"/>
                  <field name="B" fastName="b" check="b = 2" errorCode="E2"/>
                </fields>
                """, "<query action=\"checking\"><text>@@checking</text></query>"));

            Assert.Contains("'E1'", form.CheckingQuery);
            Assert.Contains("'E2'", form.CheckingQuery);
        }

        [Fact]
        public void DungCheckingSo_FieldThieuCategory_CanhBao()
        {
            ImportForm form = Parse("setCustomer", Minimal("""
                <fields name="master">
                  <field name="A" fastName="a" check="a = 1" errorCode="E1"/>
                  <field name="B" fastName="b" check="b = 2" errorCode="E2" category="3"/>
                </fields>
                """, "<query action=\"checking\"><text>@@checking(1)</text></query>"));

            Assert.Contains(form.Warnings, w => w.Contains("master.A") && w.Contains("không khai category"));
            Assert.Contains(form.Warnings, w => w.Contains("master.B") && w.Contains("@@checking(3)"));
            Assert.DoesNotContain("'E1'", form.CheckingQuery);
        }

        [Theory]
        [InlineData("<field name=\"A\" fastName=\"a\" check=\"a = 1&And;a = 2\" errorCode=\"E1\"/>", "cùng số phần tử")]
        [InlineData("<field name=\"A\" fastName=\"a\" check=\"a = 1\" errorCode=\"E1\" condition=\"x&And;y\"/>", "cùng số phần tử")]
        [InlineData("<field name=\"A\" fastName=\"a\" check=\"a = 1\"/>", "không được trống")]
        [InlineData("<field name=\"A\" fastName=\"a\" errorCode=\"E1\"/>", "không có check")]
        [InlineData("<field name=\"A\" fastName=\"a\" type=\"money\"/>", "không hỗ trợ")]
        [InlineData("<field name=\"A\" fastName=\"a b\"/>", "fastName")]
        [InlineData("<field name=\"A\" fastName=\"a\"/><field name=\"a\" fastName=\"b\"/>", "trùng field")]
        [InlineData("<field name=\"A\" fastName=\"a\" maxLength=\"0\"/>", "maxLength")]
        [InlineData("<field name=\"A\" fastName=\"a\" allowNull=\"co\"/>", "true/false")]
        public void FieldSai_LoiLucNap(string field, string message)
        {
            var ex = Assert.Throws<ImportFormException>(() => Parse("setCustomer", Minimal($"<fields name=\"master\">{field}</fields>")));
            Assert.Contains(message, ex.Message);
        }

        [Fact]
        public void EntityKhaiSai_CRThanhDauCach_BaoLechDanhSach()
        {
            // <!ENTITY And "&#13;"> làm CR thành dấu cách: check còn 1 phần tử, errorCode cũng 1 nhưng condition 2 → lệch
            string xml = Minimal("""<fields name="master"><field name="A" fastName="a" check="a = 1&And;a = 2" errorCode="E1&And;E2"/></fields>""")
                .Replace("&#38;#13;", "&#13;");

            ImportForm form = Parse("setCustomer", xml);

            Assert.Single(form.Root.Fields[0].Checks);
            Assert.Equal("a = 1 a = 2", form.Root.Fields[0].Checks[0].Check);
        }

        [Theory]
        [InlineData("<fields name=\"master\" key=\"YourID\"><field name=\"A\" fastName=\"a\"/></fields>", "không phải field")]
        [InlineData("<fields name=\"master\"><field name=\"A\" fastName=\"a\"/></fields><fields name=\"detail\"><field name=\"B\" fastName=\"b\"/></fields>", "phải khai key")]
        [InlineData("<fields name=\"master\" key=\"A\"><field name=\"A\" fastName=\"a\"/></fields><fields name=\"detail\" key=\"A\"><field name=\"B\" fastName=\"b\"/></fields>", "không được khai key")]
        [InlineData("<fields name=\"master\" key=\"A\"><field name=\"A\" fastName=\"a\"/></fields><fields name=\"Master\"><field name=\"B\" fastName=\"b\"/></fields>", "trùng")]
        [InlineData("<fields name=\"master\" key=\"A\"><field name=\"A\" fastName=\"a\"/></fields><fields name=\"detail\"><field name=\"A\" fastName=\"x\"/></fields>", "cùng fastName")]
        [InlineData("<fields name=\"master\"></fields>", "không có field")]
        [InlineData("", "ít nhất một khối")]
        public void KhoiSai_LoiLucNap(string fields, string message)
        {
            var ex = Assert.Throws<ImportFormException>(() => Parse("setCustomer", Minimal(fields)));
            Assert.Contains(message, ex.Message);
        }

        [Theory]
        [InlineData("SetCustomer", "type=\"List\"", "tên file")]
        [InlineData("setAuth", "type=\"List\"", "route hệ thống")]
        [InlineData("setCustomer", "type=\"Ledger\"", "type")]
        [InlineData("setCustomer", "type=\"1\"", "type")]
        [InlineData("setCustomer", "type=\"List\" unitAction=\"Read\"", "unitAction")]
        public void ThuocTinhImportSai_LoiLucNap(string name, string attributes, string message)
        {
            var ex = Assert.Throws<ImportFormException>(() => Parse(name, Minimal("<fields name=\"master\"><field name=\"A\" fastName=\"a\"/></fields>", rootAttributes: attributes)));
            Assert.Contains(message, ex.Message);
        }

        [Fact]
        public void PhanTuGocKhongPhaiImport_LoiLucNap()
        {
            var ex = Assert.Throws<ImportFormException>(() => ImportFormLoader.Parse("setTemplate",
                XDocument.Parse("<form type=\"Voucher\" xmlns=\"urn:schemas-fast-com:rest-api\"/>")));
            Assert.Contains("<import", ex.Message);
        }

        [Theory]
        [InlineData("type=\"Transfer\"", "", "", "phải khai <paging")]
        [InlineData("type=\"Transfer\"", "<paging pageSize=\"10\" key=\"id\"/>", "", "phải khai <paging")]
        [InlineData("type=\"List\"", "<paging pageSize=\"10\" key=\"id\"/>", "<query action=\"transferred\"><text>select 1</text></query>", "chỉ dùng cho form")]
        [InlineData("type=\"List\"", "<paging pageSize=\"10\" key=\"id; drop\"/>", "", "tên cột hợp lệ")]
        public void TransferVaPaging_KiemLucNap(string attributes, string paging, string transferred, string message)
        {
            var ex = Assert.Throws<ImportFormException>(() => Parse("transferInvoice",
                Minimal("<fields name=\"master\"><field name=\"A\" fastName=\"a\"/></fields>", paging + transferred, attributes)));
            Assert.Contains(message, ex.Message);
        }

        [Fact]
        public void Transfer_DocQueryTransferred()
        {
            ImportForm form = Parse("transferInvoice", Minimal("<fields name=\"master\"><field name=\"A\" fastName=\"a\"/></fields>",
                "<paging pageSize=\"10\" key=\"id\"/><query action=\"transferred\"><text>update x set y = 1</text></query>", "type=\"Transfer\""));

            Assert.Equal(ImportFormType.Transfer, form.Type);
            Assert.Equal("update x set y = 1", form.TransferredQuery);
            Assert.Equal("/api/v3/invoice/transfer", form.Route);
        }

        [Fact]
        public void Paging_DocDung()
        {
            ImportForm form = Parse("getInvoice", Minimal("<fields name=\"master\"><field name=\"A\" fastName=\"a\"/></fields>", "<paging pageSize=\"100\" key=\"stt_rec\"/>"));

            Assert.Equal(new ImportPaging(100, "stt_rec"), form.Paging);
            Assert.Equal("Acc", form.UnitAction);
        }

        [Fact]
        public void Load_DocFileCoEntitySystem()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-form-").FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "Options"));
                File.WriteAllText(Path.Combine(dir, "Options", "Irregular.txt"), "['\"]");
                Directory.CreateDirectory(Path.Combine(dir, "Form"));
                string file = Path.Combine(dir, "Form", "setCustomer.xml");
                File.WriteAllText(file, """
                    <?xml version="1.0" encoding="utf-8"?>
                    <!DOCTYPE import [
                      <!ENTITY And "&#38;#13;">
                      <!ENTITY IrregularValue SYSTEM "../Options/Irregular.txt">
                    ]>
                    <import xmlns="urn:schemas-fast-com:fast-api" type="List">
                      <fields name="master"><field name="A" fastName="a"/></fields>
                      <query><text>select '&IrregularValue;'</text></query>
                    </import>
                    """);

                ImportForm form = ImportFormLoader.Load(file);

                Assert.Equal("setCustomer", form.Name);
                Assert.Equal("select '['\"]'", form.ProcessingQuery);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

    public class ImportFormCatalogTests
    {
        private static ImportForm Form(string name, bool enable = true)
        {
            ImportFormLoader.TrySplitName(name, out string verb, out string resource);
            return new ImportForm
            {
                Name = name,
                Verb = verb,
                Resource = resource,
                Route = ImportFormLoader.RouteOf(verb, resource),
                Type = ImportFormType.List,
                UnitAction = ImportFormLoader.DefaultUnitAction(verb),
                Enable = enable,
                Blocks = [new ImportBlock { Name = "master", IsRoot = true, Fields = [new ImportField { Name = "A", FastName = "a" }] }]
            };
        }

        [Theory]
        [InlineData("POST", "/api/v3/sale-invoice", RouteMatch.Found, "setSaleInvoice")]
        [InlineData("post", "/API/v3/Sale-Invoice/", RouteMatch.Found, "setSaleInvoice")]
        [InlineData("POST", "/api/v3/customer/get", RouteMatch.Found, "getCustomer")]
        [InlineData("POST", "/api/v3/invoice/approve", RouteMatch.Found, "approveInvoice")]
        [InlineData("GET", "/api/v3/customer/get", RouteMatch.MethodNotAllowed, null)]
        [InlineData("DELETE", "/api/v3/sale-invoice", RouteMatch.MethodNotAllowed, null)]
        [InlineData("POST", "/api/v3/customer", RouteMatch.NotFound, null)]
        [InlineData("POST", "/api/v3/customer/set", RouteMatch.NotFound, null)]
        [InlineData("POST", "/api/v3/customer/delete", RouteMatch.NotFound, null)]
        [InlineData("POST", "/api/v3/sale_invoice", RouteMatch.NotFound, null)]
        [InlineData("POST", "/api/v3/sale-invoice/get/x", RouteMatch.NotFound, null)]
        [InlineData("POST", "/api/v3/report", RouteMatch.NotFound, null)]
        public void Resolve(string method, string path, RouteMatch expected, string? formName)
        {
            var catalog = ImportFormCatalog.Build([Form("setSaleInvoice"), Form("getCustomer"), Form("approveInvoice"), Form("setReport", enable: false)]);

            Assert.Equal(expected, catalog.Resolve(method, path, out ImportForm? form));
            Assert.Equal(formName, form?.Name);
        }

        [Fact]
        public void TrungRoute_BoCaHai()
        {
            // setSALEInvoice và setSaleInvoice cùng ra /api/v3/sale-invoice
            var catalog = ImportFormCatalog.Build([Form("setSaleInvoice"), Form("setSALEInvoice"), Form("getCustomer")]);

            Assert.Equal(RouteMatch.NotFound, catalog.Resolve("POST", "/api/v3/sale-invoice", out _));
            Assert.Contains("setSaleInvoice", catalog.Errors.Keys);
            Assert.Contains("setSALEInvoice", catalog.Errors.Keys);
            Assert.Single(catalog.Forms);
        }

        [Fact]
        public void LoadDirectory_FileLoiBiBoQua_FormKhacVanNap()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-catalog-").FullName;
            try
            {
                File.WriteAllText(Path.Combine(dir, "getCustomer.xml"), """
                    <import xmlns="urn:schemas-fast-com:fast-api" type="List">
                      <fields name="master"><field name="A" fastName="a"/></fields>
                      <query><text>select 1</text></query>
                    </import>
                    """);
                File.WriteAllText(Path.Combine(dir, "setTemplate.xml"), "<form xmlns=\"urn:schemas-fast-com:rest-api\"/>");
                File.WriteAllText(Path.Combine(dir, "setBroken.xml"), "<import");

                var catalog = ImportFormCatalog.LoadDirectory(dir);

                Assert.Equal(RouteMatch.Found, catalog.Resolve("POST", "/api/v3/customer/get", out _));
                Assert.Equal(["setBroken", "setTemplate"], catalog.Errors.Keys.Order());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
