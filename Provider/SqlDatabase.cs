using Microsoft.Data.SqlClient;
using System.Data;

namespace Provider
{
    /// <summary>
    /// Lỗi khi chạy lệnh SQL. SqlException gốc nằm ở InnerException (số lỗi, dòng, proc) để log chẩn đoán
    /// </summary>
    public sealed class DatabaseException(string message, SqlException inner) : Exception(message, inner);

    /// <summary>
    /// Truy cập SQL Server bằng ADO.NET cho một connection string. Đăng ký singleton lúc khởi động (appSettings:commandTimeout),
    /// repository nhận qua constructor thay cho lớp static dùng chung trước đây.
    /// Lệnh trên cùng một SqlConnection chạy cùng phiên SQL: bảng tạm # và transaction T-SQL giữ qua các lệnh
    /// </summary>
    public sealed class SqlDatabase
    {
        public const int DefaultCommandTimeout = 600;

        private readonly string _connectionString;

        /// <param name="commandTimeout">Giây; ≤ 0 thì dùng mặc định 600</param>
        public SqlDatabase(string connectionString, int commandTimeout)
        {
            _connectionString = connectionString;
            CommandTimeout = commandTimeout > 0 ? commandTimeout : DefaultCommandTimeout;
        }

        /// <summary>Timeout (giây) cho mọi lệnh và SqlBulkCopy async</summary>
        public int CommandTimeout { get; }

        #region Async

        public async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            SqlConnection sqlConnection = new SqlConnection(_connectionString);
            try
            {
                await sqlConnection.OpenAsync(cancellationToken);
                return sqlConnection;
            }
            catch
            {
                await sqlConnection.DisposeAsync();
                throw;
            }
        }

        public async Task ExecuteAsync(string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            await using SqlConnection sqlConnection = await OpenConnectionAsync(cancellationToken);
            await ExecuteAsync(sqlConnection, commandText, cancellationToken, parameters);
        }

        public async Task ExecuteAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            try
            {
                await using SqlCommand sqlCommand = CreateCommand(connection, commandText, parameters);
                await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database execution failed", ex);
            }
        }

        public async Task<DataSet> RetrieveAsync(string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            await using SqlConnection sqlConnection = await OpenConnectionAsync(cancellationToken);
            return await RetrieveAsync(sqlConnection, commandText, cancellationToken, parameters);
        }

        /// <summary>
        /// Đọc mọi result set vào DataSet bằng DataReader async (SqlDataAdapter không có bản async)
        /// </summary>
        public async Task<DataSet> RetrieveAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            try
            {
                await using SqlCommand sqlCommand = CreateCommand(connection, commandText, parameters);
                await using SqlDataReader reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);

                DataSet dataSet = new DataSet();
                int index = 0;
                do
                {
                    DataTable dataTable = new DataTable(index == 0 ? "Table" : $"Table{index}");
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string name = reader.GetName(i);
                        dataTable.Columns.Add(dataTable.Columns.Contains(name) || name.Length == 0 ? $"Column{i + 1}" : name, reader.GetFieldType(i));
                    }

                    object[] values = new object[reader.FieldCount];
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        reader.GetValues(values);
                        dataTable.Rows.Add(values);
                    }

                    // Bỏ qua result set không có cột (lệnh không trả dữ liệu), như SqlDataAdapter.Fill
                    if (reader.FieldCount > 0)
                    {
                        dataSet.Tables.Add(dataTable);
                        index++;
                    }
                }
                while (await reader.NextResultAsync(cancellationToken));

                return dataSet;
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database retrieve failed", ex);
            }
        }

        public async Task BulkAsync(SqlConnection connection, DataTable dataTable, string tableName, CancellationToken cancellationToken = default)
        {
            try
            {
                if (dataTable.Columns.Count > 0)
                {
                    using SqlBulkCopy bulk = new SqlBulkCopy(connection) { DestinationTableName = tableName, BulkCopyTimeout = CommandTimeout };
                    foreach (DataColumn column in dataTable.Columns)
                    {
                        bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    }

                    await bulk.WriteToServerAsync(dataTable, cancellationToken);
                }
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database bulk failed", ex);
            }
        }

        private SqlCommand CreateCommand(SqlConnection connection, string commandText, SqlParameter[] parameters)
        {
            SqlCommand sqlCommand = connection.CreateCommand();
            sqlCommand.CommandTimeout = CommandTimeout;
            sqlCommand.CommandText = commandText;
            sqlCommand.CommandType = CommandType.Text;

            if (parameters != null && parameters.Length > 0)
            {
                sqlCommand.Parameters.AddRange(parameters);
            }
            return sqlCommand;
        }

        #endregion

        #region Sync (chỉ engine v2 cũ: FormRepository, CacheRepository, UserRepository.GetUser, ServerRepository.CheckFormRights)

        public SqlConnection OpenConnection()
        {
            SqlConnection sqlConnection = new SqlConnection(_connectionString);
            try
            {
                sqlConnection.Open();
                return sqlConnection;
            }
            catch
            {
                sqlConnection.Dispose();
                throw;
            }
        }

        public void Execute(SqlConnection connection, string commandText, params SqlParameter[] parameters)
        {
            try
            {
                using SqlCommand sqlCommand = CreateCommand(connection, commandText, parameters);
                sqlCommand.ExecuteNonQuery();
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database execution failed", ex);
            }
        }

        /// <summary>Như bản cũ: không đặt BulkCopyTimeout (mặc định 30 giây của SqlBulkCopy)</summary>
        public void Bulk(SqlConnection connection, DataTable dataTable, string tableName)
        {
            try
            {
                if (dataTable.Columns.Count > 0)
                {
                    using SqlBulkCopy bulk = new SqlBulkCopy(connection) { DestinationTableName = tableName };
                    foreach (DataColumn column in dataTable.Columns)
                    {
                        bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    }

                    bulk.WriteToServer(dataTable);
                }
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database bulk failed", ex);
            }
        }

        public DataSet Retrieve(string commandText, params SqlParameter[] parameters)
        {
            using SqlConnection sqlConnection = OpenConnection();
            return Retrieve(sqlConnection, commandText, parameters);
        }

        public DataSet Retrieve(SqlConnection connection, string commandText, params SqlParameter[] parameters)
        {
            try
            {
                using SqlCommand sqlCommand = CreateCommand(connection, commandText, parameters);
                using SqlDataAdapter adapter = new SqlDataAdapter(sqlCommand);
                DataSet dataSet = new DataSet();
                adapter.Fill(dataSet);

                return dataSet;
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database retrieve failed", ex);
            }
        }

        #endregion
    }
}
