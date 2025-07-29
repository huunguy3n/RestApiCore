using Microsoft.AspNetCore.Mvc;
using Models.Interface;
using Models.Response;
using Newtonsoft.Json;
using System.Text;

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

        /// <summary>
        /// Sủ dụng trong Controller
        /// </summary>
        /// <param name="controller"></param>
        /// <param name="response"></param>
        /// <returns></returns>
        public static IActionResult ToActionResult(ControllerBase controller, IResponse response)
        {
            if (response is ResponseQuery responseQuery)
            {
                var json = JsonConvert.SerializeObject(response);
                
                return new ContentResult
                {
                    StatusCode = response.statusCode,
                    Content = json,
                    ContentType = "application/json; charset=utf-8"
                };
            }
            return controller.StatusCode(response.statusCode, response);
        }

        /// <summary>
        /// Sử dụng trong ActionFilter
        /// </summary>
        /// <param name="response"></param>
        /// <returns></returns>
        public static ContentResult ToContentResult(IResponse response)
        {
            return new ContentResult
            {
                StatusCode = response.statusCode,
                Content = JsonConvert.SerializeObject(response),
                ContentType = "application/json; charset=utf-8"
            };
        }
    }
}
