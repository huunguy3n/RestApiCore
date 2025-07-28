using Models.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Response
{
    public class ResponseQuery : IResponse
    {
        public string code { get; set; }
        public bool success { get; set; }
        public string message { get; set; }
        public int httpStatusCode { get; set; }
        public string cacheID { get; set; }
        public Dictionary<string, ResponseData> data { get; set; }


        public ResponseQuery()
        {
            code = "NONE";
            success = false;
            message = string.Empty;
            httpStatusCode = 200;
            cacheID = string.Empty;
            data = new Dictionary<string, ResponseData>();
        }

        public ResponseQuery(IResponse response)
        {
            code = response.code;
            success = response.success;
            message = response.message;
            httpStatusCode = response.httpStatusCode;
            cacheID = string.Empty;
            data = new Dictionary<string, ResponseData>();
        }
    }
}
