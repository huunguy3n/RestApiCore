using System.Data;

namespace Business.V3
{
    /// <summary>Một trang: bảng theo tên, cùng thứ tự với dataset gốc; Keys = giá trị key (chứng từ) của trang, theo thứ tự</summary>
    public sealed record DataPage(IReadOnlyDictionary<string, DataTable> Tables, IReadOnlyList<string> Keys);

    public sealed record PagedResult(IReadOnlyList<DataPage> Pages, int PageSize, int TotalRowCount, int KeyCount);

    /// <summary>
    /// Phân trang dataset nhiều bảng (KE_HOACH_V3 4.4): cắt theo giá trị key của bảng dữ liệu đầu tiên, mỗi trang tối đa pageSize
    /// giá trị key; mỗi trang kèm mọi dòng cùng key ở các bảng khác nên một chứng từ không bị tách sang hai trang.
    /// Bảng không có cột key chỉ trả ở trang 0
    /// </summary>
    public static class DataSetPager
    {
        public static PagedResult Paginate(IReadOnlyDictionary<string, DataTable> tables, string key, int pageSize)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

            int totalRows = tables.Values.Sum(t => t.Rows.Count);
            DataTable? first = tables.Values.FirstOrDefault();
            if (first == null || !first.Columns.Contains(key))
            {
                // Không cắt được theo key: trả một trang (người khai form sửa key)
                return new PagedResult([new DataPage(tables, [])], pageSize, totalRows, first?.Rows.Count ?? 0);
            }

            // Giá trị key theo thứ tự xuất hiện ở bảng đầu
            List<string> keys = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (DataRow row in first.Rows)
            {
                string value = KeyOf(row, key);
                if (seen.Add(value))
                {
                    keys.Add(value);
                }
            }

            int pageCount = Math.Max(1, (keys.Count + pageSize - 1) / pageSize);
            Dictionary<string, int> pageOfKey = new(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                pageOfKey[keys[i]] = i / pageSize;
            }

            // Mỗi bảng có key: chia dòng vào trang theo key; key không có ở bảng đầu thì bỏ (không thuộc chứng từ nào)
            List<Dictionary<string, DataTable>> pages = Enumerable.Range(0, pageCount).Select(_ => new Dictionary<string, DataTable>()).ToList();
            foreach (var (name, table) in tables)
            {
                bool hasKey = table.Columns.Contains(key);
                for (int p = 0; p < pageCount; p++)
                {
                    // Trang nào cũng có đủ tên bảng (rỗng nếu không có dòng) để client đọc cấu trúc cố định
                    pages[p][name] = table.Clone();
                }

                foreach (DataRow row in table.Rows)
                {
                    int page = hasKey
                        ? pageOfKey.GetValueOrDefault(KeyOf(row, key), -1)
                        : 0;
                    if (page >= 0)
                    {
                        pages[page][name].ImportRow(row);
                    }
                }
            }

            return new PagedResult(
                pages.Select((p, i) => new DataPage(p, keys.Skip(i * pageSize).Take(pageSize).Where(k => k != NullKey).ToList())).ToList(),
                pageSize, totalRows, keys.Count);
        }

        /// <summary>Key NULL gom một nhóm nhưng không đưa vào Keys (không đánh dấu được)</summary>
        private const string NullKey = "\0null";

        private static string KeyOf(DataRow row, string key) => row[key] is DBNull ? NullKey : row[key].ToString()!;
    }
}
