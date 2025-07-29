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
        public int statusCode { get; set; }
        public string cacheID { get; set; }
        public ResponseData data { get; set; }


        public ResponseQuery()
        {
            code = "NONE";
            success = false;
            message = string.Empty;
            statusCode = 200;
            cacheID = string.Empty;
            data = new ResponseData();
        }

        public ResponseQuery(IResponse response)
        {
            code = response.code;
            success = response.success;
            message = response.message;
            statusCode = response.statusCode;
            cacheID = string.Empty;
            data = new ResponseData();
        }
    }
}
