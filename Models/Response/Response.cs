using Models.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Response
{
    public class Response : IResponse
    {
        public string code { get; set; }
        public bool success { get; set; }
        public string message { get; set; }
        public int httpStatusCode { get; set; }
    }
}
