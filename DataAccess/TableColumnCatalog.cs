using System.Data;
using Microsoft.Data.SqlClient;
using Provider;

namespace DataAccess
{
    /// <summary>
    /// Tra tên cột của bảng qua INFORMATION_SCHEMA (chỉ đọc, nhớ theo bảng). Dùng khi nâng form v2 → v3 để tách master/detail theo cột thật
    /// (V2FormUpgrader, UpgradeOptions.ColumnsOf). Kết nối qua Provider.SqlDatabase
    /// </summary>
    public sealed class TableColumnCatalog : IDisposable, IAsyncDisposable
    {
        private readonly SqlDatabase _database;
        private readonly SqlConnection _connection;
        private readonly Dictionary<string, IReadOnlySet<string>?> _cache = new(StringComparer.OrdinalIgnoreCase);

        private TableColumnCatalog(SqlDatabase database, SqlConnection connection)
        {
            _database = database;
            _connection = connection;
        }

        public static TableColumnCatalog Open(string connectionString) =>
            Open(new SqlDatabase(connectionString, 60));

        public static TableColumnCatalog Open(SqlDatabase database)
        {
            SqlConnection connection = database.OpenConnectionAsync().GetAwaiter().GetResult();
            return new TableColumnCatalog(database, connection);
        }

        public static async Task<TableColumnCatalog> OpenAsync(SqlDatabase database, CancellationToken cancellationToken = default)
        {
            SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            return new TableColumnCatalog(database, connection);
        }

        public IReadOnlySet<string>? ColumnsOf(string table)
        {
            if (_cache.TryGetValue(table, out IReadOnlySet<string>? known))
            {
                return known;
            }
            DataSet data = _database.RetrieveAsync(_connection,
                "SELECT RTRIM(a.COLUMN_NAME) AS COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS a WHERE a.TABLE_NAME = @table",
                CancellationToken.None,
                new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = table }).GetAwaiter().GetResult();
            HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in data.Tables[0].Rows)
            {
                columns.Add(Convert.ToString(row["COLUMN_NAME"])!);
            }
            return _cache[table] = columns.Count > 0 ? columns : null;
        }

        public void Dispose() => _connection.Dispose();

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
