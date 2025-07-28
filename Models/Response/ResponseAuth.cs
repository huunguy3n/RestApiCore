using Models.Interface;

namespace Models.Response
{
    public class ResponseAuth : IResponse
    {
        public string code { get; set; }
        public bool success { get; set; }
        public string message { get; set; }
        public int statusCode { get; set; }
        public string access_token { get; set; }
        public string token_type { get; set; }
        public int expires_in { get; set; }

        public ResponseAuth()
        {
            code = "NONE";
            success = false;
            message = string.Empty;
            statusCode = 200;
            access_token = string.Empty;
            token_type = string.Empty;
            expires_in = 0;
        }

        public ResponseAuth(IResponse response)
        {
            code = response.code;
            success = response.success;
            message = response.message;
            statusCode = response.statusCode;
            access_token = string.Empty;
            token_type = string.Empty;
            expires_in = 0;
        }
    }
}
