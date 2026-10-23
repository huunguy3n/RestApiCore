using DataAccess;
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
        public async Task WriteLogDataAsync(string guid, long durationMs, Dictionary<string, string> infoRequest, Dictionary<string, string> paramsRequest, Dictionary<string, string> headersRequest, string bodyRequest,
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
            // LogRepository tự cắt theo độ dài cột

            Dictionary<string, object> response = new()
            {
                { "statusCode", responseStatusCode },
                { "headers", responseHeader },
                { "body", responseBody }
            };
            string jsonResponse = Newtonsoft.Json.JsonConvert.SerializeObject(response);

            LogRepository logRepository = new(appConfig);
            await logRepository.InsertLogDataAsync(guid, durationMs, infoRequest["ip"], infoRequest["userName"], infoRequest["method"], infoRequest["path"], jsonRequest, jsonResponse);
        }

        //LogError(action, errorMessage, stackTrace)
        public async Task WriteLogErrorAsync(string path, string errorMessage, string stackTrace)
        {
            LogRepository logRepository = new(appConfig);
            await logRepository.InsertLogErrorAsync(path, errorMessage, stackTrace);
        }

        //LogTransfer(userName, form, key, message, status)
        public void WriteLogTransfer(IRequest request, string key, string message, bool status)
        {
            // Implement logging logic for data transfer
        }
    }
}
