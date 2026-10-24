using System.Xml.Linq;
using Business;
using Models;

namespace Tests
{
    public class V2FormUpgraderTests
    {
        private const string Sale = """
            <?xml version="1.0" encoding="utf-8"?>
            <import xmlns="urn:schemas-fast-com:fast-api">
              <fields table="apikeymap" type="Voucher" voucherCode="HDA">
                <field name="YourId" fastName="id_doitac" type="string" maxLength="36" allowNull="false"/>
                <field name="VcDate" fastName="ngay_ct" type="datetime" allowNull="false"/>
                <field name="Status" fastName="status" maxLength="1" type="string" justContent="0, 1, 2"/>
                <field name="ItemID" fastName="ma_vt" type="string" maxLength="36" allowNull="false"/>
                <field name="Quantity" fastName="so_luong" type="numeric" allowNull="false"/>
                <field name="DepartID" fastName="ma_bp" type="string" maxLength="16"/>
                <field name="einvoiceNumber" fastName="so_ct_hddt" type="string" maxLength="12"/>
                <field name="ma_nt" maxLength="3" type="string" defaultValue="" hidden="true"/>
              </fields>
              <query><text><![CDATA[
            declare @c bit
            exec api_checkBefore @@control, @@table, @@type, @@voucherCode, @@primaryKey, @@userId, '', '', @c output
            if @c = 1 exec api_ProcessDataFor_m81 @@voucherCode, @@userId, @@sysDatabaseName
            ]]></text></query>
            </import>
            """;

        private static readonly Dictionary<string, HashSet<string>> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["m81$000000"] = new(StringComparer.OrdinalIgnoreCase) { "id_doitac", "ngay_ct", "status", "ma_bp", "so_ct_hddt", "ma_nt" },
            ["d81$000000"] = new(StringComparer.OrdinalIgnoreCase) { "ma_vt", "so_luong", "ma_bp" }
        };

        private static readonly UpgradeOptions Catalog = new(t => Columns.GetValueOrDefault(t));

        private static (ImportForm Form, UpgradeResult Result) Upgrade(string xml = Sale, UpgradeOptions? options = null)
        {
            UpgradeResult result = V2FormUpgrader.Upgrade("setSalesInvoices", XDocument.Parse(xml), options);
            return (ImportFormLoader.Parse("setSalesInvoices", XDocument.Parse(result.Xml)), result);
        }

        [Fact]
        public void NhanDienV2_BoQuaV3()
        {
            Assert.True(V2FormUpgrader.IsV2(XDocument.Parse(Sale)));
            Assert.False(V2FormUpgrader.IsV2(XDocument.Parse("""
                <import xmlns="urn:schemas-fast-com:fast-api" type="List"><fields name="master"><field name="a" fastName="a"/></fields></import>
                """)));
        }

        [Fact]
        public void ChungTu_TachMasterDetailTheoCotThat()
        {
            var (form, result) = Upgrade(options: Catalog);

            Assert.True(result.Valid, string.Join("; ", result.Notes));
            Assert.Equal(ImportFormType.Voucher, form.Type);
            Assert.Equal("HDA", form.VoucherCode);
            Assert.Equal(["master", "detail"], form.Blocks.Select(b => b.Name));
            Assert.Equal("m81$000000", form.Blocks[0].Table);
            Assert.Equal("d81$000000", form.Blocks[1].Table);
            Assert.Equal("yourId", form.Blocks[0].Key);
            Assert.Equal(["yourId", "vcDate", "status", "einvoiceNumber", "maNt"], form.Blocks[0].Fields.Select(f => f.Name));
            // ma_bp có ở cả hai bảng: theo field đứng trước (so_luong thuộc detail); yourId là khóa tự thêm
            Assert.Equal(["yourId", "itemID", "quantity", "departID"], form.Blocks[1].Fields.Select(f => f.Name));
        }

        [Fact]
        public void KhongCoCatalog_VaoMasterVaBaoLoi()
        {
            var (form, result) = Upgrade();

            Assert.True(result.Valid);
            Assert.Single(form.Blocks);
            Assert.Equal("", form.Blocks[0].Table);
            Assert.Contains(result.Notes, n => n.Contains("--config"));
        }

        [Fact]
        public void JustContent_ThanhCheck_VaFieldAnGiuMacDinh()
        {
            var (form, _) = Upgrade(options: Catalog);

            ImportField status = form.Blocks[0].Fields.Single(f => f.Name == "status");
            Assert.Equal("status not in ('0', '1', '2')", status.Checks.Single().Check);
            Assert.Equal("INVALID_FIELD", status.Checks.Single().ErrorCode);
            Assert.False(status.JustContent);
            Assert.Contains("status not in", form.CheckingQuery);

            ImportField hidden = form.Blocks[0].Fields.Single(f => f.Name == "maNt");
            Assert.True(hidden.Hidden);
            Assert.Equal("ma_nt", hidden.FastName);
            Assert.Equal("", hidden.DefaultValue);
        }

        [Fact]
        public void Processing_KhongChayProcV2_GiuLamChuThich()
        {
            var (form, result) = Upgrade(options: Catalog);

            string[] lines = form.ProcessingQuery.Replace("\r", "").Split('\n');
            Assert.All(lines.Where(l => l.Contains("api_")), l => Assert.StartsWith("--", l.TrimStart()));
            Assert.Contains("SELECT 'ERROR'", form.ProcessingQuery);
            Assert.Contains(result.Notes, n => n.Contains("api_ProcessDataFor_m81") && n.Contains("#master, #detail"));
        }

        [Fact]
        public void DanhMuc_GiuBang_VaKeyLaKhoaChinh()
        {
            var (form, result) = Upgrade("""
                <import xmlns="urn:schemas-fast-com:fast-api">
                  <fields table="dmkh" type="List">
                    <field name="YourId" fastName="id_doitac" maxLength="36" allowNull="false" type="string"/>
                    <field name="CusCode" fastName="ma_kh" maxLength="16" type="string" allowNull="false" isPrimaryKey="true"/>
                    <field name="SuplierYN" fastName="cc_yn" type="bool"/>
                  </fields>
                  <query><text>exec api_ProcessDataForList @@table, @@key, @@userId</text></query>
                </import>
                """);

            Assert.True(result.Valid, string.Join("; ", result.Notes));
            Assert.Equal("dmkh", form.Blocks[0].Table);
            Assert.Equal("cusCode", form.Blocks[0].Key);
            Assert.Equal(ImportFieldType.Bool, form.Blocks[0].Fields[2].Type);
        }

        [Fact]
        public void Report_BoTableVaCacheId()
        {
            var (form, result) = Upgrade("""
                <import xmlns="urn:schemas-fast-com:fast-api">
                  <fields table="dmkh" type="Report">
                    <field name="cacheID"/>
                    <field name="GLDateFrom" fastName="ngay_ct1" type="datetime" allowNull="false"/>
                    <field name="Status" fastName="status" type="string" maxLength="1" justContent="1, 0, *"/>
                  </fields>
                  <query><text>select 1</text></query>
                </import>
                """);

            Assert.True(result.Valid, string.Join("; ", result.Notes));
            Assert.Equal("filter", form.Blocks[0].Name);
            Assert.Equal("", form.Blocks[0].Table);
            Assert.Equal(["glDateFrom", "status"], form.Blocks[0].Fields.Select(f => f.Name));
            Assert.Contains(result.Notes, n => n.Contains("cacheID"));
        }

        [Fact]
        public void Load_PhanGiaiEntityTrongInclude()
        {
            string dir = Directory.CreateTempSubdirectory("fastapi-v2-").FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "Include"));
                File.WriteAllText(Path.Combine(dir, "Include", "Fields.txt"), """
                    <field name="id" fastName="id_doitac" type="string" maxLength="36"/>
                    <field name="&ref;" fastName="so_tc_ddv"/>
                    """);
                File.WriteAllText(Path.Combine(dir, "Include", "Irregular.txt"), "<![CDATA['''`~!@#$%^&*(),;\"<>?/\\,_']]>");
                string file = Path.Combine(dir, "setX.xml");
                File.WriteAllText(file, """
                    <?xml version="1.0" encoding="utf-8"?>
                    <!DOCTYPE import [
                      <!ENTITY Fields SYSTEM "Include\Fields.txt">
                      <!ENTITY Irregular SYSTEM "Include\Irregular.txt">
                      <!ENTITY ref "billNumber">
                    ]>
                    <import xmlns="urn:schemas-fast-com:fast-api">
                      <fields table="dmkh" type="List">&Fields;</fields>
                      <query><text><![CDATA[select ]]>&Irregular;<![CDATA[, 1]]></text></query>
                    </import>
                    """);

                XDocument document = V2FormUpgrader.Load(file);
                Assert.True(V2FormUpgrader.IsV2(document));

                UpgradeResult result = V2FormUpgrader.Upgrade("setX", document);
                ImportForm form = ImportFormLoader.Parse("setX", XDocument.Parse(result.Xml));
                Assert.Equal(["id", "billNumber"], form.Blocks[0].Fields.Select(f => f.Name));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("YourId", "yourId")]
        [InlineData("CusID", "cusID")]
        [InlineData("GLDateFrom", "glDateFrom")]
        [InlineData("ma_nt", "maNt")]
        [InlineData("fcode4", "fcode4")]
        [InlineData("s6", "s6")]
        public void CamelCase(string input, string expected)
        {
            Assert.Equal(expected, V2FormUpgrader.CamelCase(input));
        }

        [Fact]
        public void KhongPhaiV2_BaoLoi()
        {
            Assert.Throws<ArgumentException>(() => V2FormUpgrader.Upgrade("setX", XDocument.Parse("<import xmlns=\"urn:schemas-fast-com:fast-api\"/>")));
        }
    }
}
