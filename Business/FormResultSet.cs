using System.Data;

namespace Business
{
    /// <summary>
    /// Đọc dataset của query processing theo quy ước KE_HOACH 4.4: bảng 0 có cột code, message, tables;
    /// bảng 1..N là dữ liệu, tên lấy theo tables (phân tách bằng dấu phẩy) hoặc data / data1..dataN.
    /// Số bảng và cột do SQL của từng dự án quyết định nên dữ liệu giữ DataTable (adapter có chủ đích cho form động)
    /// </summary>
    public sealed class FormResultSet
    {
        public const string Success = "SUCCESS";

        private FormResultSet(string code, string message, Dictionary<string, DataTable> tables)
        {
            Code = code;
            Message = message;
            Tables = tables;
        }

        /// <summary>Cột code của bảng 0; không có thì SUCCESS</summary>
        public string Code { get; }

        /// <summary>Cột message của bảng 0 (có thể rỗng)</summary>
        public string Message { get; }

        /// <summary>Bảng dữ liệu theo tên, đúng thứ tự trong dataset</summary>
        public IReadOnlyDictionary<string, DataTable> Tables { get; }

        public bool IsSuccess => Code == Success;

        public int RowCount => Tables.Values.Sum(t => t.Rows.Count);

        /// <summary>Bảng 0 cột code; không có thì coi là SUCCESS</summary>
        public static string CodeOf(DataSet data) =>
            data.Tables.Count > 0 && data.Tables[0].Columns.Contains("code") && data.Tables[0].Rows.Count > 0
                ? data.Tables[0].Rows[0]["code"]?.ToString()?.Trim() ?? Success
                : Success;

        public static FormResultSet From(DataSet data)
        {
            DataTable? head = data.Tables.Count > 0 ? data.Tables[0] : null;
            string message = head != null && head.Columns.Contains("message") && head.Rows.Count > 0 ? head.Rows[0]["message"]?.ToString() ?? "" : "";
            string[] names = head != null && head.Columns.Contains("tables") && head.Rows.Count > 0
                ? (head.Rows[0]["tables"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            Dictionary<string, DataTable> tables = [];
            int dataCount = Math.Max(0, data.Tables.Count - 1);
            for (int i = 1; i < data.Tables.Count; i++)
            {
                string name = i - 1 < names.Length ? names[i - 1] : dataCount == 1 ? "data" : $"data{i}";
                tables[name] = data.Tables[i];
            }

            return new FormResultSet(CodeOf(data), message, tables);
        }
    }
}
