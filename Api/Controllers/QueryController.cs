using Business;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Interface;
using Models.Options;
using Models.Request;
using Web.Custom;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class QueryController(AppConfig appConfig, Options options, IMemoryCache cache, ResponseManager responseManager) : ControllerBase
    {
        //POST api/query/controllerName?cacheID=123&pageIndex=1
        //header: unit, debug
        //body: raw json
        [HttpPost("{controllerName}")]
        public IActionResult Query(string controllerName)
        {
            var request = RequestHelper.GetRequest(Request);

            Dictionary<string, string> info = request["info"];
            Dictionary<string, string> @params = request["params"];
            Dictionary<string, string> headers = request["headers"];
            Dictionary<string, string> body = request["body"];

            RequestQuery requestQuery = new(controllerName, info["ip"], info["userName"]);

            string? value = string.Empty;
            //Get values from params (cacheID, pageIndex)
            requestQuery.cacheID = @params.TryGetValue("cacheID", out value) ? value : "";
            requestQuery.pageIndex = @params.TryGetValue("pageIndex", out value) ? int.TryParse(value, out int pageIndex) ? pageIndex : -1 : -1;

            // Get values from the headers (unit, debug)
            requestQuery.unit = headers.TryGetValue("unit", out value) ? value : "";
            requestQuery.debug = (headers.TryGetValue("debug", out value) ? value : "") == "1";

            //Get the request body as a string (json)
            requestQuery.jsonBody = body["raw"];

            if (string.IsNullOrEmpty(requestQuery.unit))
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("MISSING_PARAMETER"));
            }

            if (requestQuery.cacheID != "" && requestQuery.pageIndex < 0)
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("MISSING_PARAMETER"));
            }

            FormManager formManager = new(appConfig, options, responseManager);
            IResponse response = formManager.ExecuteFrom(controllerName, requestQuery);

            return ResponseHelper.ToActionResult(this, response);
        }
    }
}
