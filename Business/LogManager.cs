using Microsoft.Data.SqlClient;
using Models;
using Models.Interface;

namespace Business
{
    public class LogManager(AppConfig appConfig)
    {
        public void WriteLog(IRequest request, IResponse response)
        {
            // Implement logging logic here
        }

        //LogActivity(userName, action)
        public void WriteLogActivity(IRequest request)
        {
            // Implement logging logic for user activity
        }

        /// <summary>
        /// Lưu thông tin log từ request và response.
        /// Thông tin lưu ở SQL và file json
        /// </summary>
        public void WriteLogData(string guid, long durationMs, Dictionary<string, string> infoRequest, Dictionary<string, string> paramsRequest, Dictionary<string, string> headersRequest, string bodyRequest,
            Dictionary<string, string> responseHeader, int responseStatusCode, string responseBody)
        {
            //Lưu ở SQL các thông tin
            //timeStamp, ipAddress, userName, method, path, durationMs, metadataRequest, metadataResponse, guid
    
            Dictionary<string, object> request = new()
            {
                { "info", infoRequest },
                { "params", paramsRequest },
                { "headers", headersRequest },
                { "body", bodyRequest }
            };
            string jsonRequest = Newtonsoft.Json.JsonConvert.SerializeObject(request);
            string metadataRequest = jsonRequest.Length > 4000 ? jsonRequest.Substring(0, 3999) : jsonRequest;

            Dictionary<string, object> response = new()
            {
                { "statusCode", responseStatusCode },
                { "headers", responseHeader },
                { "body", responseBody }
            };
            string jsonResponse = Newtonsoft.Json.JsonConvert.SerializeObject(response);
            string metadataResponse = jsonResponse.Length > 4000 ? jsonResponse.Substring(0, 3999) : jsonResponse;

            using (var connection = Provider.Provider.OpenConn(appConfig.appConnectionString))
            {
                Provider.Provider.Execute(connection, "create table #logData(timeStamp datetime, ipAddress varchar(32), userName varchar(32), method varchar(32), path nvarchar(128), durationMs numeric(19,2), metadataRequest nvarchar(4000), metadataResponse nvarchar(4000), guid varchar(36))");
                Provider.Provider.Execute(connection, $"insert #logData values(getdate(), N'{infoRequest["ip"]}', N'{infoRequest["userName"]}', N'{infoRequest["method"]}', N'{infoRequest["path"]}', {durationMs}, @metadataRequest, @metadataResponse, @guid)",
                    new SqlParameter { ParameterName = "@metadataRequest", Value = metadataRequest },
                    new SqlParameter { ParameterName = "@metadataResponse", Value = metadataResponse },
                    new SqlParameter { ParameterName = "@guid", Value = guid }
                    );
                Provider.Provider.Execute(connection, "exec restApi_InsertLogData '#logData'");
                Provider.Provider.CloseConn(connection);
            }

            //Lưu ở file json các thông tin
            string logDirectory = Path.Combine(appConfig.pathDirectory, $"Log/{{0}}/{DateTime.Now.ToString("yyyyMMdd")}");

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

        //LogError(action, errorMessage, stackTrace)
        public void WriteLogError(string path, string errorMessage, string stackTrace)
        {
            errorMessage = errorMessage.Length > 4000 ? errorMessage.Substring(0, 3999) : errorMessage;
            stackTrace = stackTrace.Length > 4000 ? stackTrace.Substring(0, 3999) : stackTrace;

            using (var connection = Provider.Provider.OpenConn(appConfig.appConnectionString))
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

        //LogTransfer(userName, form, key, message, status)
        public void WriteLogTransfer(IRequest request, string key, string message, bool status)
        {
            // Implement logging logic for data transfer
        }
    }
}
