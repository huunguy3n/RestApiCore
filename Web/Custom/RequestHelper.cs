using System.Text;

namespace Web.Custom
{
    public static class RequestHelper
    {
        public static Dictionary<string, Dictionary<string, string>> GetRequest(HttpRequest request)
        {
            Dictionary<string, Dictionary<string, string>> requestData = new();

            Dictionary<string, string> info = new()
            {
                { "method", request.Method },
                { "path", request.Path },
                { "ip", request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW" },
                { "userName", request.HttpContext.User.Identity?.Name ?? "anonymous" }
            };

            //params
            Dictionary<string, string> @params = new();

            foreach (var param in request.Query)
            {
                string key = param.Key;
                string value = param.Value.ToString();

                @params.Add(key, value);
            }


            //headers
            Dictionary<string, string> headers = new();

            foreach (var header in request.Headers)
            {
                if (header.Key == "Authorization" || header.Key == "unit" || header.Key == "debug" || header.Key == "User-Agent" || header.Key == "time" || header.Key == "key")
                {
                    string key = header.Key;
                    string value = header.Value.ToString();

                    headers.Add(key, value);
                }
            }

            //body
            Dictionary<string, string> body = new();
            if (request.Body.CanRead)
            {
                //string bodyContent = new StreamReader(request.Body).ReadToEndAsync().Result;
                request.EnableBuffering(); // Cho phép đọc nhiều lần

                request.Body.Position = 0; // Đặt con trỏ về đầu

                using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string bodyContent = reader.ReadToEndAsync().Result;

                request.Body.Position = 0; // Đặt lại lần nữa để middleware/controller phía sau có thể đọc tiếp

                body.Add("raw", bodyContent);
            }
            else
            {
                body.Add("raw", "");
            }

            requestData.Add("info", info);
            requestData.Add("params", @params);
            requestData.Add("headers", headers);
            requestData.Add("body", body);

            return requestData;
        }
    }
}
