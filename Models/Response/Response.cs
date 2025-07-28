using Models.Interface;

namespace Models.Response
{
    public class Response : IResponse
    {
        public string code { get; set; }
        public bool success { get; set; }
        public string message { get; set; }
        public int statusCode { get; set; }

        public Response()
        {
            code = "NONE";
            success = false;
            message = string.Empty;
            statusCode = 200;
        }
    }
}
