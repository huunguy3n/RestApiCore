using Microsoft.Data.SqlClient;
using System.Data;

namespace Provider
{
    public static class Provider
    {
        public static int CommandTimeout { get; set; } = 600;
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
    }
}
