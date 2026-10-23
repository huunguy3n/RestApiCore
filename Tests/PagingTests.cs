using System.Data;
using System.Text.Json;
using Business.V3;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Models;

namespace Tests
{
    public class DataSetPagerTests
    {
        private static DataTable Table(string name, IEnumerable<object[]> rows, params string[] columns)
        {
            DataTable table = new(name);
            foreach (string column in columns)
            {
                table.Columns.Add(column, column == "so_luong" ? typeof(int) : typeof(string));
            }
            foreach (object[] row in rows)
            {
                table.Rows.Add(row);
            }
            return table;
        }

        /// <summary>Nghiệm thu pha 4: 3 bảng × 10.000 dòng lấy đủ qua các trang, không tách chứng từ</summary>
        [Fact]
        public void BaBang10000Dong_DuDong_KhongTachChungTu()
        {
            var random = new Random(42);
            string Key(int i) => $"HD{i:D5}";
            var tables = new Dictionary<string, DataTable>
            {
                ["master"] = Table("master", Enumerable.Range(0, 10000).Select(i => new object[] { Key(i), $"KH{i % 50}" }), "stt_rec", "ma_kh"),
                ["detail"] = Table("detail", Enumerable.Range(0, 10000).Select(i => new object[] { Key(random.Next(10000)), $"VT{i}", i }), "stt_rec", "ma_vt", "so_luong"),
                ["tax"] = Table("tax", Enumerable.Range(0, 10000).Select(i => new object[] { Key(random.Next(10000)), "10" }), "stt_rec", "thue_suat")
            };

            PagedResult paged = DataSetPager.Paginate(tables, "stt_rec", 100);

            Assert.Equal(100, paged.Pages.Count);
            Assert.Equal(30000, paged.TotalRowCount);
            Assert.Equal(10000, paged.KeyCount);
            foreach (string name in tables.Keys)
            {
                Assert.Equal(10000, paged.Pages.Sum(p => p.Tables[name].Rows.Count));
                Assert.All(paged.Pages, p => Assert.True(p.Tables.ContainsKey(name), "trang nào cũng đủ tên bảng"));
            }

            // Mỗi chứng từ nằm trọn trong đúng một trang, ở mọi bảng
            var pageOfKey = new Dictionary<string, int>();
            for (int p = 0; p < paged.Pages.Count; p++)
            {
                Assert.True(paged.Pages[p].Tables["master"].Rows.Count <= 100);
                foreach (DataTable table in paged.Pages[p].Tables.Values)
                {
                    foreach (DataRow row in table.Rows)
                    {
                        string key = row["stt_rec"].ToString()!;
                        Assert.Equal(p, pageOfKey.TryAdd(key, p) ? p : pageOfKey[key]);
                    }
                }
            }
        }

        [Fact]
        public void BangDauLaChiTiet_DemTheoKey_KhongTheoDong()
        {
            var tables = new Dictionary<string, DataTable>
            {
                ["detail"] = Table("detail", [["A", "1", 1], ["A", "2", 1], ["A", "3", 1], ["B", "1", 1], ["C", "1", 1]], "stt_rec", "ma_vt", "so_luong")
            };

            PagedResult paged = DataSetPager.Paginate(tables, "stt_rec", 2);

            Assert.Equal(2, paged.Pages.Count);
            Assert.Equal(4, paged.Pages[0].Tables["detail"].Rows.Count);
            Assert.Equal(1, paged.Pages[1].Tables["detail"].Rows.Count);
        }

        [Fact]
        public void BangKhongCoKey_ChiOTrang0_KeyLaBiBo()
        {
            var tables = new Dictionary<string, DataTable>
            {
                ["master"] = Table("master", [["A", "x"], ["B", "y"], ["C", "z"]], "stt_rec", "ma_kh"),
                ["summary"] = Table("summary", [["tong", "3"]], "ten", "gia_tri"),
                ["detail"] = Table("detail", [["A", "1", 1], ["X", "2", 1], ["C", "3", 1]], "stt_rec", "ma_vt", "so_luong")
            };

            PagedResult paged = DataSetPager.Paginate(tables, "stt_rec", 2);

            Assert.Equal(1, paged.Pages[0].Tables["summary"].Rows.Count);
            Assert.Equal(0, paged.Pages[1].Tables["summary"].Rows.Count);
            Assert.Equal(2, paged.Pages.Sum(p => p.Tables["detail"].Rows.Count));
            Assert.Equal(["master", "summary", "detail"], paged.Pages[1].Tables.Keys);
        }

        [Fact]
        public void BangDauKhongCoCotKey_MotTrang()
        {
            var tables = new Dictionary<string, DataTable> { ["data"] = Table("data", [["a"], ["b"], ["c"]], "x") };

            PagedResult paged = DataSetPager.Paginate(tables, "stt_rec", 1);

            Assert.Single(paged.Pages);
            Assert.Equal(3, paged.Pages[0].Tables["data"].Rows.Count);
        }

        [Fact]
        public void KhongCoDong_MotTrangRong()
        {
            var tables = new Dictionary<string, DataTable> { ["master"] = Table("master", [], "stt_rec") };

            PagedResult paged = DataSetPager.Paginate(tables, "stt_rec", 10);

            Assert.Single(paged.Pages);
            Assert.Equal(0, paged.TotalRowCount);
        }
    }

    public class CursorStoreTests
    {
        private static CursorStore Create()
        {
            var services = new ServiceCollection();
            services.AddHybridCache();
            return new CursorStore(new AppConfig { cursorTtlMinutes = 5 }, services.BuildServiceProvider().GetRequiredService<HybridCache>());
        }

        [Fact]
        public async Task LuuVaDocTrang_ChiDungUser()
        {
            CursorStore store = Create();
            DataTable table = new("master");
            table.Columns.Add("stt_rec");
            for (int i = 0; i < 5; i++)
            {
                table.Rows.Add($"HD{i}");
            }
            PagedResult paged = DataSetPager.Paginate(new Dictionary<string, DataTable> { ["master"] = table }, "stt_rec", 2);

            string cursor = await store.SaveAsync(7, "getInvoice", "CTY", paged);

            Assert.Equal(new CursorInfo("getInvoice", "CTY", 3, 2, 5, 5), await store.GetInfoAsync(7, cursor));
            string? json = await store.GetPageJsonAsync(7, cursor, 2);
            Assert.Equal("HD4", JsonDocument.Parse(json!).RootElement.GetProperty("master")[0].GetProperty("stt_rec").GetString());

            Assert.Null(await store.GetInfoAsync(8, cursor));
            Assert.Null(await store.GetPageJsonAsync(8, cursor, 0));
            Assert.Null(await store.GetInfoAsync(7, "khongco"));
            Assert.Null(await store.GetInfoAsync(7, "khongco"));
        }
    }
}
