using Microsoft.Data.SqlClient;
using Models;
using System.Data;

namespace DataAccess
{
    public class LogRepository
    {
        private readonly AppConfig _appConfig;
        public LogRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
        }

        /// <summary>
        /// Mọi giá trị đi qua tham số có kiểu và độ dài khớp cột #logData (sửa lỗi 1.4 #3: ip, userName, path ghép chuỗi)
        /// </summary>
        public async Task InsertLogDataAsync(string guid, long durationMs, string ip, string userName, string method, string path, string metadataRequest, string metadataResponse, CancellationToken cancellationToken = default)
        {
            await using var connection = await Provider.Provider.OpenConnAsync(_appConfig.appConnectionString, cancellationToken);

            await Provider.Provider.ExecuteAsync(connection, "create table #logData(timeStamp datetime, ipAddress varchar(32), userName varchar(32), method varchar(32), path nvarchar(128), durationMs numeric(19,2), metadataRequest nvarchar(4000), metadataResponse nvarchar(4000), guid varchar(36))", cancellationToken);
            await Provider.Provider.ExecuteAsync(connection, "insert #logData values(getdate(), @ipAddress, @userName, @method, @path, @durationMs, @metadataRequest, @metadataResponse, @guid)", cancellationToken,
                Parameter("@ipAddress", SqlDbType.VarChar, 32, ip),
                Parameter("@userName", SqlDbType.VarChar, 32, userName),
                Parameter("@method", SqlDbType.VarChar, 32, method),
                Parameter("@path", SqlDbType.NVarChar, 128, path),
                new SqlParameter("@durationMs", SqlDbType.Decimal) { Precision = 19, Scale = 2, Value = durationMs },
                Parameter("@metadataRequest", SqlDbType.NVarChar, 4000, metadataRequest),
                Parameter("@metadataResponse", SqlDbType.NVarChar, 4000, metadataResponse),
                Parameter("@guid", SqlDbType.VarChar, 36, guid)
                );
            await Provider.Provider.ExecuteAsync(connection, "exec restApi_InsertLogData '#logData'", cancellationToken);
        }

        public void WriteLogData(string guid, string jsonRequest, string jsonResponse)
        {
            //Lưu ở file json các thông tin
            string logDirectory = Path.Combine(_appConfig.pathDirectory, $"Log/{{0}}/{DateTime.Now.ToString("yyyyMMdd")}");

            Directory.CreateDirectory(string.Format(logDirectory, "Request"));
            string logPathFile = Path.Combine(string.Format(logDirectory, "Request"), $"{guid}.json");
            using (StreamWriter sw = new StreamWriter(logPathFile))
            {
                sw.WriteLine(jsonRequest);
            }

            Directory.CreateDirectory(string.Format(logDirectory, "Response"));
            logPathFile = Path.Combine(string.Format(logDirectory, "Response"), $"{guid}.json");
            using (StreamWriter sw = new StreamWriter(logPathFile))
            {
                sw.WriteLine(jsonResponse);
            }
        }

        public void RemoveLogData()
        {
            DataTable logData = new();
            using (var connection = Provider.Provider.OpenConn(_appConfig.appConnectionString))
            {
                logData = Provider.Provider.Retrieve(connection, "exec restApi_RemoveLogData").Tables[0];
                Provider.Provider.CloseConn(connection);
            }
            //Xóa file log
            foreach (DataRow row in logData.Rows)
            {
                string guid = row["guid"].ToString();
                string logDirectory = Path.Combine(_appConfig.pathDirectory, $"Log/{{0}}/{DateTime.Now.ToString("yyyyMMdd")}");
                string requestFilePath = Path.Combine(string.Format(logDirectory, "Request"), $"{guid}.json");
                string responseFilePath = Path.Combine(string.Format(logDirectory, "Response"), $"{guid}.json");
                if (File.Exists(requestFilePath))
                {
                    File.Delete(requestFilePath);
                }
                if (File.Exists(responseFilePath))
                {
                    File.Delete(responseFilePath);
                }
            }
        }

        public async Task InsertLogErrorAsync(string path, string errorMessage, string stackTrace, CancellationToken cancellationToken = default)
        {
            await using var connection = await Provider.Provider.OpenConnAsync(_appConfig.appConnectionString, cancellationToken);

            await Provider.Provider.ExecuteAsync(connection, "create table #logError(timeStamp datetime, path nvarchar(128), message nvarchar(4000), stacktrace nvarchar(4000))", cancellationToken);
            await Provider.Provider.ExecuteAsync(connection, "insert #logError values(getdate(), @path, @errorMessage, @stackTrace)", cancellationToken,
                Parameter("@path", SqlDbType.NVarChar, 128, path),
                Parameter("@errorMessage", SqlDbType.NVarChar, 4000, errorMessage),
                Parameter("@stackTrace", SqlDbType.NVarChar, 4000, stackTrace)
                );
            await Provider.Provider.ExecuteAsync(connection, "exec restApi_InsertLogError '#logError'", cancellationToken);
        }

        /// <summary>
        /// Tham số chuỗi cắt theo độ dài cột để không lỗi "String or binary data would be truncated"
        /// </summary>
        private static SqlParameter Parameter(string name, SqlDbType type, int size, string? value)
        {
            object dbValue = value == null ? DBNull.Value : (value.Length > size ? value.Substring(0, size) : value);
            return new SqlParameter(name, type, size) { Value = dbValue };
        }
    }
}
