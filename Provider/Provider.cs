using Microsoft.Data.SqlClient;
using System.Data;

namespace Provider
{
    public static class Provider
    {
        /// <summary>
        /// Timeout (giây) mặc định cho mọi lệnh, đặt một lần lúc khởi động qua Configure (appSettings:commandTimeout).
        /// Không gán lại ở từng repository: biến static dùng chung giữa các request
        /// </summary>
        public static int CommandTimeout { get; private set; } = 600;

        public static void Configure(int commandTimeout)
        {
            if (commandTimeout > 0)
            {
                CommandTimeout = commandTimeout;
            }
        }

        #region Async

        public static async Task<SqlConnection> OpenConnAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            SqlConnection sqlConnection = new SqlConnection(connectionString);
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

        public static async Task ExecuteAsync(string connectionString, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            await using SqlConnection sqlConnection = await OpenConnAsync(connectionString, cancellationToken);
            await ExecuteAsync(sqlConnection, commandText, cancellationToken, parameters);
        }

        public static async Task ExecuteAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            try
            {
                await using SqlCommand sqlCommand = CreateCommand(connection, commandText, parameters);
                await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex)
            {
                throw new Exception("Database execution failed", ex);
            }
        }

        public static async Task<DataSet> RetrieveAsync(string connectionString, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
        {
            await using SqlConnection sqlConnection = await OpenConnAsync(connectionString, cancellationToken);
            return await RetrieveAsync(sqlConnection, commandText, cancellationToken, parameters);
        }

        /// <summary>
        /// Đọc mọi result set vào DataSet bằng DataReader async (SqlDataAdapter không có bản async)
        /// </summary>
        public static async Task<DataSet> RetrieveAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken = default, params SqlParameter[] parameters)
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
                throw new Exception("Database retrieve failed", ex);
            }
        }

        public static async Task BulkAsync(SqlConnection connection, DataTable dataTable, string tableName, CancellationToken cancellationToken = default)
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
                throw new Exception("Database bulk failed", ex);
            }
        }

        private static SqlCommand CreateCommand(SqlConnection connection, string commandText, SqlParameter[] parameters)
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

        #region Sync (dùng tới khi FormRepository viết lại ở pha 3)

        public static SqlConnection OpenConn(string connectionString)
        {
            if (!string.IsNullOrEmpty(connectionString))
            {
                SqlConnection sqlConnection = new SqlConnection(connectionString);
                sqlConnection.Open();
                return sqlConnection;
            }
            return null;
        }

        public static void CloseConn(SqlConnection sqlConnection)
        {
            if (sqlConnection != null && sqlConnection.State == ConnectionState.Open)
            {
                sqlConnection.Close();
            }
        }

        public static void Execute(string connectionString, string commandText, params SqlParameter[] parameters)
        {
            SqlConnection sqlConnection = OpenConn(connectionString);
            Execute(sqlConnection, commandText, parameters);
            CloseConn(sqlConnection);
        }

        public static void Execute(SqlConnection connection, string commandText, params SqlParameter[] parameters)
        {
            try
            {
                SqlCommand sqlCommand = connection.CreateCommand();
                sqlCommand.CommandTimeout = CommandTimeout;
                sqlCommand.CommandText = commandText;
                sqlCommand.CommandType = CommandType.Text;

                if (parameters != null && parameters.Length > 0)
                {
                    sqlCommand.Parameters.AddRange(parameters);
                }

                sqlCommand.ExecuteNonQuery();
            }
            catch (SqlException ex)
            {
                throw new Exception("Database execution failed", ex);
            }
        }

        public static void Bulk(SqlConnection connection, DataSet dataSet)
        {
            try
            {
                foreach (DataTable dataTable in dataSet.Tables)
                {
                    if (dataTable.Columns.Count > 0)
                    {
                        SqlBulkCopy bulk = new SqlBulkCopy(connection);
                        bulk.DestinationTableName = dataTable.TableName;
                        foreach (DataColumn column in dataTable.Columns)
                        {
                            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                        }

                        bulk.WriteToServer(dataTable);
                    }
                }
            }
            catch (SqlException ex)
            {
                throw new Exception("Database bulk failed", ex);
            }
        }
        public static void Bulk(SqlConnection connection, DataTable dataTable, string tableName)
        {
            try
            {
                if (dataTable.Columns.Count > 0)
                {
                    SqlBulkCopy bulk = new SqlBulkCopy(connection);
                    bulk.DestinationTableName = tableName;
                    foreach (DataColumn column in dataTable.Columns)
                    {
                        bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    }

                    bulk.WriteToServer(dataTable);
                }
            }
            catch (SqlException ex)
            {
                throw new Exception("Database bulk failed", ex);
            }
        }

        public static DataSet Retrieve(string connectionString, string commandText, params SqlParameter[] parameters)
        {
            SqlConnection sqlConnection = OpenConn(connectionString);
            DataSet dataSet = Retrieve(sqlConnection, commandText, parameters);
            CloseConn(sqlConnection);
            return dataSet;
        }

        public static DataSet Retrieve(SqlConnection connection, string commandText, params SqlParameter[] parameters)
        {
            try
            {
                SqlCommand sqlCommand = connection.CreateCommand();
                sqlCommand.CommandTimeout = CommandTimeout;
                sqlCommand.CommandText = commandText;
                sqlCommand.CommandType = CommandType.Text;

                if (parameters != null && parameters.Length > 0)
                {
                    sqlCommand.Parameters.AddRange(parameters);
                }

                SqlDataAdapter adapter = new SqlDataAdapter(sqlCommand);
                DataSet dataSet = new DataSet();
                adapter.Fill(dataSet);

                return dataSet;
            }
            catch (SqlException ex)
            {
                throw new Exception("Database retrieve failed", ex);
            }
        }

        #endregion
    }
}
