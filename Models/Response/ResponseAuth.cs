using Models.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Response
{
    public class ResponseAuth : IResponse
    {
        public string code { get; set; }
        public bool success { get; set; }
        public string message { get; set; }
        public int httpStatusCode { get; set; }
        public string access_token { get; set; }
        public string token_type { get; set; }
        public int expires_in { get; set; }

        public ResponseAuth()
        {
            code = "NONE";
            success = false;
            message = string.Empty;
            httpStatusCode = 200;
            access_token = string.Empty;
            token_type = string.Empty;
            expires_in = 0;
        }

        public ResponseAuth(IResponse response)
        {
            code = response.code;
            success = response.success;
            message = response.message;
            httpStatusCode = response.httpStatusCode;
            access_token = string.Empty;
            token_type = string.Empty;
            expires_in = 0;
        }
    }
}
