using Models.Interface;
using Newtonsoft.Json;

namespace Web.Custom
{
    public static class ResponseHelper
    {
        /// <summary>
        /// Sử dụng trong Middleware
        /// </summary>
        /// <param name="context"></param>
        /// <param name="response"></param>
        /// <returns></returns>
        public static async Task WriteJsonAsync(HttpContext context, IResponse response)
        {
            context.Response.StatusCode = response.statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";

            var json = JsonConvert.SerializeObject(response);
            await context.Response.WriteAsync(json);
        }

        /// <summary>Ghi JSON đã dựng sẵn (response thành công của endpoint tự định dạng)</summary>
        public static async Task WriteRawAsync(HttpContext context, int status, string json)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(json);
        }
    }
}
