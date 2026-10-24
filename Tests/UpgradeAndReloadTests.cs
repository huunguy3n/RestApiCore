using System.Xml.Linq;
using Business;
using Microsoft.Extensions.Logging.Abstractions;
using Models;
using Web.Custom;

namespace Tests
{
    public class V2FormUpgraderTests
    {
        private static readonly Dictionary<string, ControllerLookup> Controllers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["customer"] = new("dmkh", "ma_kh"),
            ["Item"] = new("dmvt", "ma_vt")
        };

        private const string V2Form = """
            <form type="Voucher" id="HDA" xmlns="urn:schemas-fast-com:rest-api">
              <struct name="master" baseTable="m81$000000">
                <field name="yourId" mapping="id_doitac" maxLength="36" isPrimaryKey="true" allowNulls="false"/>
                <field name="voucherDate" mapping="ngay_ct" type="DateTime" dataFormatString="dd/MM/yyyy" allowNulls="false"/>
                <field name="customerId" mapping="customer_id" maxLength="36" allowNulls="false">
                  <reference name="ma_kh" controller="customer" index="0"/>
                </field>
                <field name="salesman" mapping="nv_id"><reference name="ma_nvbh" controller="Employee" index="0"/></field>
                <field name="ma_kh" mapping="" hidden="true"/>
                <field name="flag" mapping="" type="Boolean"/>
              </struct>
              <struct name="detail" baseTable="d81$000000">
                <field name="yourId" mapping="id_doitac" maxLength="36" allowNulls="false"/>
                <field name="itemId" mapping="item_id"><reference name="ma_vt" controller="Item" index="1"/></field>
                <field name="ma_vt" mapping="" hidden="true"/>
                <field name="promotion" mapping="km_yn" type="Int" defaultValue="0">
                  <check condition="km_yn not in (0, 1)" errorCode="INVALID_FIELD" index="1"/>
                  <check condition="km_yn = 1 and so_luong = 0" errorCode="PROMOTION_QTY" index="1"/>
                </field>
                <field name="quantity" mapping="so_luong" type="Decimal"/>
                <field name="note" mapping="ghi_chu" type="Money"/>
              </struct>
              <queries>
                <query action="Checking"><text>@@update(0) @@checking(0) @@update(1) @@checking(1)</text></query>
                <query action="Processing"><text>exec p @@id, @@unit, @@userERP, @@userName, '@@appDatabaseName'</text></query>
              </queries>
            </form>
            """;

        private static (ImportForm Form, UpgradeResult Result) Upgrade()
        {
            UpgradeResult result = V2FormUpgrader.Upgrade("setSaleInvoice", XDocument.Parse(V2Form), Controllers);
            return (ImportFormLoader.Parse("setSaleInvoice", XDocument.Parse(result.Xml)), result);
        }

        [Fact]
        public void ChuyenDuoc_VaNapDuocBangLoaderV3()
        {
            var (form, result) = Upgrade();

            Assert.True(result.Valid);
            Assert.Equal(ImportFormType.Voucher, form.Type);
            Assert.Equal("HDA", form.VoucherCode);
            Assert.Equal("yourId", form.Root.Key);
            Assert.Equal("m81$000000", form.Root.Table);
        }

        [Fact]
        public void Reference_ThanhCheckTonTaiMa_BoFieldAn()
        {
            var (form, result) = Upgrade();

            ImportField customer = form.Root.Fields.Single(f => f.Name == "customerId");
            Assert.Equal("ma_kh", customer.FastName);
            Assert.True(customer.UpperCase);
            Assert.Null(customer.MaxLength);
            Assert.Equal(0, customer.Category);
            Assert.Equal(new ImportCheck("ma_kh not in (select ma_kh from dmkh)", "", "", "CUSTOMER_NOT_FOUND"), customer.Checks.Single());
            Assert.DoesNotContain(form.Root.Fields, f => f.Name == "ma_kh");
            Assert.Contains(result.Notes, n => n.Contains("Đối tác phải gửi mã FBO (ma_kh)"));
        }

        [Fact]
        public void ControllerKhongCoTrongUpdateXml_GhiChu_KhongCheck()
        {
            var (form, result) = Upgrade();

            ImportField salesman = form.Root.Fields.Single(f => f.Name == "salesman");
            Assert.Equal("ma_nvbh", salesman.FastName);
            Assert.Empty(salesman.Checks);
            Assert.Contains(result.Notes, n => n.Contains("controller 'Employee'"));
        }

        [Fact]
        public void CheckV2_ThanhDanhSachV3_KhoiConBoKhoaTrung()
        {
            var (form, result) = Upgrade();
            ImportBlock detail = form.Blocks[1];

            Assert.True(detail.Fields[0].IsInheritedKey, "yourId của khối con do v3 tự thêm");
            ImportField promotion = detail.Fields.Single(f => f.Name == "promotion");
            Assert.Equal(["INVALID_FIELD", "PROMOTION_QTY"], promotion.Checks.Select(c => c.ErrorCode));
            Assert.Equal(1, promotion.Category);
            Assert.Equal(ImportFieldType.Int, promotion.Type);
            Assert.Equal("0", promotion.DefaultValue);
            Assert.Equal(ImportFieldType.Numeric, detail.Fields.Single(f => f.Name == "quantity").Type);
            Assert.Contains(result.Notes, n => n.Contains("'Money'"));
            Assert.Contains(result.Notes, n => n.Contains("flag: v2 không có mapping"));
        }

        [Fact]
        public void Query_BoUpdate_DoiBienV2()
        {
            var (form, result) = Upgrade();

            Assert.DoesNotContain("@@update", form.CheckingQuery);
            Assert.Contains("ITEM_NOT_FOUND", form.CheckingQuery);
            Assert.Equal("exec p @@voucherCode, @@unit, @@userName, @@apiUserName, '@@appDatabaseName'", form.ProcessingQuery);
            Assert.Contains(result.Notes, n => n.Contains("@@apiUserName"));
            Assert.Contains(result.Notes, n => n.Contains("trong dấu nháy"));
            Assert.Equal(result.Notes.Count, result.Notes.Distinct().Count());
        }

        [Fact]
        public void ReadControllers_TuUpdateXml()
        {
            var controllers = V2FormUpgrader.ReadControllers(XDocument.Parse("""
                <update xmlns="urn:schemas-fast-com:data-update">
                  <controller name="customer"><text>--update @@table set @@reference = b.ma_kh from @@table a join dmkh b with(nolock) on a.@@field = b.id_doitac</text></controller>
                  <controller name="lot"><text>update @@table set @@reference = b.ma_lo from @@table a join dmlo b on a.@@field = b.id_doitac</text></controller>
                  <controller name="odd"><text>select 1</text></controller>
                </update>
                """));

            Assert.Equal(new ControllerLookup("dmkh", "ma_kh"), controllers["customer"]);
            Assert.Equal(new ControllerLookup("dmlo", "ma_lo"), controllers["lot"]);
            Assert.False(controllers.ContainsKey("odd"));
        }

        [Theory]
        [InlineData("customer", "CUSTOMER_NOT_FOUND")]
        [InlineData("SaleEmployee", "SALE_EMPLOYEE_NOT_FOUND")]
        public void ErrorCodeOf(string controller, string code)
        {
            Assert.Equal(code, V2FormUpgrader.ErrorCodeOf(controller));
        }

        [Fact]
        public void KhongPhaiV2_BaoLoi()
        {
            Assert.Throws<ArgumentException>(() => V2FormUpgrader.Upgrade("setX", XDocument.Parse("<import xmlns=\"urn:schemas-fast-com:fast-api\"/>"), Controllers));
        }
    }

    public class FormReloaderTests
    {
        private const string Form = """
            <import xmlns="urn:schemas-fast-com:fast-api" type="List">
              <fields name="master"><field name="a" fastName="a"/></fields>
              <query><text>select 1</text></query>
            </import>
            """;

        [Fact]
        public async Task FileDoi_NapLaiCatalog()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-reload-").FullName;
            try
            {
                ImportFormCatalog Load() => ImportFormCatalog.LoadDirectory(dir);
                var runtime = new FormRuntime(Load(), null);
                using var reloader = new FormReloader(runtime, Load, dir, NullLogger<FormReloader>.Instance) { Debounce = TimeSpan.FromMilliseconds(50) };
                await reloader.StartAsync(CancellationToken.None);

                Assert.Equal(RouteMatch.NotFound, runtime.Catalog.Resolve("POST", "/api/customer/get", out _));

                File.WriteAllText(Path.Combine(dir, "getCustomer.xml"), Form);
                Assert.True(await WaitUntil(() => runtime.Catalog.Resolve("POST", "/api/customer/get", out _) == RouteMatch.Found), "thêm file → có route");

                File.Delete(Path.Combine(dir, "getCustomer.xml"));
                Assert.True(await WaitUntil(() => runtime.Catalog.Resolve("POST", "/api/customer/get", out _) == RouteMatch.NotFound), "xoá file → mất route");

                await reloader.StopAsync(CancellationToken.None);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task NapLaiLoi_GiuCatalogCu()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-reload-").FullName;
            try
            {
                File.WriteAllText(Path.Combine(dir, "getCustomer.xml"), Form);
                ImportFormCatalog initial = ImportFormCatalog.LoadDirectory(dir);
                var runtime = new FormRuntime(initial, null);
                int calls = 0;
                ImportFormCatalog Failing()
                {
                    calls++;
                    throw new InvalidOperationException("mất kết nối SQL");
                }

                using var reloader = new FormReloader(runtime, Failing, dir, NullLogger<FormReloader>.Instance) { Debounce = TimeSpan.FromMilliseconds(50) };
                await reloader.StartAsync(CancellationToken.None);

                File.WriteAllText(Path.Combine(dir, "setOther.xml"), Form);
                Assert.True(await WaitUntil(() => calls > 0));

                Assert.Same(initial, runtime.Catalog);
                await reloader.StopAsync(CancellationToken.None);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static async Task<bool> WaitUntil(Func<bool> condition)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition())
                {
                    return true;
                }
                await Task.Delay(50);
            }
            return condition();
        }
    }
}
