using System.Data;
using System.Xml.Linq;
using Business;
using Models;

namespace Tests
{
    /// <summary>Quy ước tên khai ở &lt;import&gt;: JSON camelCase, cột SQL snake_case</summary>
    public class NamingConventionTests
    {
        private static ImportForm Parse(string root, string field) => ImportFormLoader.Parse("setCustomer", XDocument.Parse($"""
            <import xmlns="urn:schemas-fast-com:fast-api" type="List" {root}>
              <fields name="master">{field}</fields>
              <query action="processing"><text>select 'SUCCESS' as code</text></query>
            </import>
            """));

        [Theory]
        [InlineData("yourId", true)]
        [InlineData("yourID", true)]
        [InlineData("YourId", false)]
        [InlineData("your_id", false)]
        [InlineData("", false)]
        public void IsCamelCase_KiemTenJson(string name, bool expected) => Assert.Equal(expected, NamingConvention.IsCamelCase(name));

        [Theory]
        [InlineData("ma_kh", true)]
        [InlineData("so_ct0", true)]
        [InlineData("MaKh", false)]
        [InlineData("ma__kh", false)]
        [InlineData("ma-kh", false)]
        public void IsSnakeCase_KiemTenCot(string name, bool expected) => Assert.Equal(expected, NamingConvention.IsSnakeCase(name));

        [Fact]
        public void KhongKhai_MacDinhCamelVaSnake()
        {
            ImportForm form = Parse("", """<field name="customerId" fastName="ma_kh"/>""");

            Assert.Equal("camelCase", form.JsonNaming);
            Assert.Equal("snake_case", form.ColumnNaming);
        }

        [Theory]
        [InlineData("""<field name="CustomerId" fastName="ma_kh"/>""", "camelCase")]
        [InlineData("""<field name="customer_id" fastName="ma_kh"/>""", "camelCase")]
        [InlineData("""<field name="customerId" fastName="maKh"/>""", "snake_case")]
        [InlineData("""<field name="customerId" fastName="Ma_Kh"/>""", "snake_case")]
        public void TenSaiQuyUoc_LoiLucNap(string field, string message)
        {
            var ex = Assert.Throws<ImportFormException>(() => Parse("jsonNaming=\"camelCase\" columnNaming=\"snake_case\"", field));

            Assert.Contains(message, ex.Message);
        }

        [Fact]
        public void TenKhoiPhaiCamelCase()
        {
            var ex = Assert.Throws<ImportFormException>(() => ImportFormLoader.Parse("setCustomer", XDocument.Parse("""
                <import xmlns="urn:schemas-fast-com:fast-api" type="List">
                  <fields name="Master"><field name="a" fastName="a"/></fields>
                  <query action="processing"><text>select 'SUCCESS' as code</text></query>
                </import>
                """)));

            Assert.Contains("camelCase", ex.Message);
        }

        [Theory]
        [InlineData("jsonNaming=\"snake_case\"", "jsonNaming")]
        [InlineData("columnNaming=\"camelCase\"", "columnNaming")]
        public void KieuKhaiKhongHoTro_LoiLucNap(string attribute, string message)
        {
            var ex = Assert.Throws<ImportFormException>(() => Parse(attribute, """<field name="a" fastName="a"/>"""));

            Assert.Contains(message, ex.Message);
        }

        [Fact]
        public void KetQuaQuery_TenBangVaCotKhongCamel_BaoLoi()
        {
            DataSet data = new();
            DataTable head = data.Tables.Add("head");
            head.Columns.Add("code");
            head.Columns.Add("message");
            head.Columns.Add("tables");
            head.Rows.Add("SUCCESS", "", "sale_lines,lines");
            DataTable first = data.Tables.Add("a");
            first.Columns.Add("customerId");
            first.Columns.Add("total_amount");
            DataTable second = data.Tables.Add("b");
            second.Columns.Add("CustomerName");

            FormResultSet result = FormResultSet.From(data);

            Assert.Equal(["tên bảng 'sale_lines'", "cột 'sale_lines.total_amount'", "cột 'lines.CustomerName'"], result.FormatProblems);
        }

        [Fact]
        public void KetQuaQuery_DungCamel_KhongCoLoi()
        {
            DataSet data = new();
            DataTable head = data.Tables.Add("head");
            head.Columns.Add("code");
            head.Rows.Add("SUCCESS");
            data.Tables.Add("a").Columns.Add("customerName");

            Assert.Empty(FormResultSet.From(data).FormatProblems);
        }
    }
}
