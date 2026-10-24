using Models.Interface;
using Models.Options;
using Models.Response;

namespace Business
{
    /// <summary>
    /// Mã kết quả → response chuẩn (code, success, message, statusCode) theo Message.xml.
    /// Mã không có trong Message.xml thành EXCEPTION / 400
    /// </summary>
    public class ResponseManager(Options options)
    {
        public IResponse GetResponse(string code)
        {
            Message message = options.Messages.Get(code);
            Response response = new();

            if (message != null)
            {
                response.code = message.code;
                response.success = message.success;
                response.message = message.message;
                response.statusCode = message.httpStatusCode;
            }
            else
            {
                response.code = "EXCEPTION";
                response.success = false;
                response.message = "Exception";
                response.statusCode = 400;
            }

            return response;
        }

        public IResponse GetResponse(string code, string message)
        {
            IResponse response = GetResponse(code);
            if (!string.IsNullOrEmpty(message)) response.message = message;

            return response;
        }
    }
}
