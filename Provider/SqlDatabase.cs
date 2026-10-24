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
    /// Lệnh trên cùng một SqlConnection chạy cùng phiên SQL: bảng tạm # và transaction T-SQL giữ qua các lệnh.
    /// Sau OpenConnectionAsync chỉ gọi API của lớp này — không new SqlCommand / SqlBulkCopy bên ngoài
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

        public Task<SqlTransaction> BeginTransactionAsync(SqlConnection connection, CancellationToken cancellationToken = default) =>
            BeginTransactionAsync(connection, IsolationLevel.ReadCommitted, cancellationToken);

        public async Task<SqlTransaction> BeginTransactionAsync(SqlConnection connection, IsolationLevel isolationLevel, CancellationToken cancellationToken = default) =>
            (SqlTransaction)await connection.BeginTransactionAsync(isolationLevel, cancellationToken);

        public async Task ExecuteAsync(string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            await using SqlConnection sqlConnection = await OpenConnectionAsync(cancellationToken);
            await ExecuteAsync(sqlConnection, commandText, cancellationToken, parameters);
        }

        public Task ExecuteAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters) =>
            ExecuteAsync(connection, transaction: null, commandText, cancellationToken, parameters);

        public async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            try
            {
                await using SqlCommand sqlCommand = CreateCommand(connection, transaction, commandText, parameters);
                await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database execution failed", ex);
            }
        }

        public async Task<object?> ExecuteScalarAsync(string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            await using SqlConnection sqlConnection = await OpenConnectionAsync(cancellationToken);
            return await ExecuteScalarAsync(sqlConnection, commandText, cancellationToken, parameters);
        }

        public Task<object?> ExecuteScalarAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters) =>
            ExecuteScalarAsync(connection, transaction: null, commandText, cancellationToken, parameters);

        public async Task<object?> ExecuteScalarAsync(SqlConnection connection, SqlTransaction? transaction, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            try
            {
                await using SqlCommand sqlCommand = CreateCommand(connection, transaction, commandText, parameters);
                object? value = await sqlCommand.ExecuteScalarAsync(cancellationToken);
                return value == DBNull.Value ? null : value;
            }
            catch (SqlException ex)
            {
                throw new DatabaseException("Database scalar failed", ex);
            }
        }

        public async Task<DataSet> RetrieveAsync(string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            await using SqlConnection sqlConnection = await OpenConnectionAsync(cancellationToken);
            return await RetrieveAsync(sqlConnection, commandText, cancellationToken, parameters);
        }

        public Task<DataSet> RetrieveAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters) =>
            RetrieveAsync(connection, transaction: null, commandText, cancellationToken, parameters);

        /// <summary>
        /// Đọc mọi result set vào DataSet bằng DataReader async (SqlDataAdapter không có bản async)
        /// </summary>
        public async Task<DataSet> RetrieveAsync(SqlConnection connection, SqlTransaction? transaction, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            try
            {
                await using SqlCommand sqlCommand = CreateCommand(connection, transaction, commandText, parameters);
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

        public Task BulkAsync(SqlConnection connection, DataTable dataTable, string tableName, CancellationToken cancellationToken = default) =>
            BulkAsync(connection, transaction: null, dataTable, tableName, cancellationToken);

        public async Task BulkAsync(SqlConnection connection, SqlTransaction? transaction, DataTable dataTable, string tableName, CancellationToken cancellationToken = default)
        {
            try
            {
                if (dataTable.Columns.Count > 0)
                {
                    using SqlBulkCopy bulk = transaction == null
                        ? new SqlBulkCopy(connection) { DestinationTableName = tableName, BulkCopyTimeout = CommandTimeout }
                        : new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = tableName, BulkCopyTimeout = CommandTimeout };
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

        private SqlCommand CreateCommand(SqlConnection connection, SqlTransaction? transaction, string commandText, SqlParameter[] parameters)
        {
            SqlCommand sqlCommand = connection.CreateCommand();
            sqlCommand.CommandTimeout = CommandTimeout;
            sqlCommand.CommandText = commandText;
            sqlCommand.CommandType = CommandType.Text;
            if (transaction != null)
            {
                sqlCommand.Transaction = transaction;
            }

            if (parameters != null && parameters.Length > 0)
            {
                sqlCommand.Parameters.AddRange(parameters);
            }
            return sqlCommand;
        }

        #endregion
    }
}
