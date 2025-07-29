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
            Provider.Provider.CommandTimeout = _appConfig.commandTimeout;
        }

        public void InsertLogData(string guid, long durationMs, string ip, string userName, string method, string path, string metadataRequest, string metadataResponse, string jsonRequest, string jsonResponse)
        {
            using (var connection = Provider.Provider.OpenConn(_appConfig.appConnectionString))
            {
                Provider.Provider.Execute(connection, "create table #logData(timeStamp datetime, ipAddress varchar(32), userName varchar(32), method varchar(32), path nvarchar(128), durationMs numeric(19,2), metadataRequest nvarchar(4000), metadataResponse nvarchar(4000), guid varchar(36))");
                Provider.Provider.Execute(connection, $"insert #logData values(getdate(), N'{ip}', N'{userName}', N'{method}', N'{path}', {durationMs}, @metadataRequest, @metadataResponse, @guid)",
                    new SqlParameter { ParameterName = "@metadataRequest", Value = metadataRequest },
                    new SqlParameter { ParameterName = "@metadataResponse", Value = metadataResponse },
                    new SqlParameter { ParameterName = "@guid", Value = guid }
                    );
                Provider.Provider.Execute(connection, "exec restApi_InsertLogData '#logData'");
                Provider.Provider.CloseConn(connection);
            }
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

        public void InsertLogError(string path, string errorMessage, string stackTrace)
        {
            errorMessage = errorMessage.Length > 4000 ? errorMessage.Substring(0, 3999) : errorMessage;
            stackTrace = stackTrace.Length > 4000 ? stackTrace.Substring(0, 3999) : stackTrace;
            using (var connection = Provider.Provider.OpenConn(_appConfig.appConnectionString))
            {
                Provider.Provider.Execute(connection, "create table #logError(timeStamp datetime, path nvarchar(128), message nvarchar(4000), stacktrace nvarchar(4000))");
                Provider.Provider.Execute(connection, $"insert #logError values(getdate(), N'{path}', @errorMessage, @stackTrace)",
                    new SqlParameter { ParameterName = "@errorMessage", Value = errorMessage },
                    new SqlParameter { ParameterName = "@stackTrace", Value = stackTrace }
                    );
                Provider.Provider.Execute(connection, "exec restApi_InsertLogError '#logError'");
                Provider.Provider.CloseConn(connection);
            }
        }
    }
}
