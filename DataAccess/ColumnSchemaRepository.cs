using Microsoft.Data.SqlClient;
using Models;
using System.Data;

namespace DataAccess
{
    /// <summary>
    /// Đọc cấu trúc cột bảng FBO cho form v3 (KE_HOACH 2.3). Kiểu do người dùng định nghĩa quy về kiểu gốc
    /// </summary>
    public class ColumnSchemaRepository(AppConfig appConfig)
    {
        /// <returns>null nếu bảng không tồn tại</returns>
        public async Task<IReadOnlyDictionary<string, ColumnInfo>?> GetColumnsAsync(string table, CancellationToken cancellationToken = default)
        {
            var ds = await Provider.Provider.RetrieveAsync(appConfig.appConnectionString, """
                SELECT a.name, b.name AS type_name, a.max_length, a.precision, a.scale
                    FROM sys.columns a
                    JOIN sys.types b ON b.user_type_id = a.system_type_id
                    WHERE a.object_id = OBJECT_ID(@table) AND OBJECTPROPERTY(a.object_id, 'IsUserTable') = 1
                """, cancellationToken,
                new SqlParameter("@table", SqlDbType.NVarChar, 256) { Value = table });

            if (ds.Tables.Count == 0 || ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }

            Dictionary<string, ColumnInfo> columns = new(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in ds.Tables[0].Rows)
            {
                string typeName = row["type_name"].ToString()!;
                int maxLength = Convert.ToInt32(row["max_length"]);
                if (maxLength > 0 && typeName is "nchar" or "nvarchar")
                {
                    maxLength /= 2;
                }

                string name = row["name"].ToString()!;
                columns[name] = new ColumnInfo(name, typeName, maxLength, Convert.ToInt32(row["precision"]), Convert.ToInt32(row["scale"]));
            }
            return columns;
        }
    }
}
