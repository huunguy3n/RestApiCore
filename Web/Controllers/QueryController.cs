using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class QueryController : ControllerBase
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;

        public QueryController(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }

        //POST api/query/controllerName?cacheID=123&pageIndex=1
        //header: unit, debug
        //body: raw json
        [HttpPost("{controllerName}")]
        public HttpResponseMessage Query(string controllerName)
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);
            responseManager.OnResponseGenerated += LogManager.WriteLog;

            string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            RequestQuery requestQuery = new RequestQuery(controllerName, ipAddress, User.Identity.Name);

            // Get values from the headers (unit, debug)
            Request.Headers.TryGetValue("unit", out var oUnit);
            Request.Headers.TryGetValue("debug", out var oDebug);

            //Get the request body as a string (json)
            requestQuery.jsonBody = new StreamReader(Request.Body).ReadToEndAsync().Result;

            //Get values from params (cacheID, pageIndex)
            Request.Query.TryGetValue("cacheID", out var oCacheID);
            Request.Query.TryGetValue("pageIndex", out var oPageIndex);


            if (!StringValues.IsNullOrEmpty(oUnit))
            {
                requestQuery.unit = oUnit.FirstOrDefault() ?? "";
            }
            if (!StringValues.IsNullOrEmpty(oDebug))
            {
                requestQuery.debug = (oDebug.FirstOrDefault() ?? "") == "1";
            }
            if (!StringValues.IsNullOrEmpty(oCacheID))
            {
                requestQuery.cacheID = oCacheID.FirstOrDefault() ?? "";
            }
            if (!StringValues.IsNullOrEmpty(oPageIndex))
            {
                if (int.TryParse(oPageIndex.FirstOrDefault(), out int pageIndex))
                {
                    requestQuery.pageIndex = pageIndex;
                }
                else
                {
                    requestQuery.pageIndex = -1;
                }
            }

            if (string.IsNullOrEmpty(requestQuery.unit))
            {
                return responseManager.GetResponseMessage(requestQuery, "MISSING_PARAMETER");
            }

            if (requestQuery.cacheID != "" && requestQuery.pageIndex < 0)
            {
                return responseManager.GetResponseMessage(requestQuery, "MISSING_PARAMETER");
            }

            FormManager formManager = new FormManager(_appConfig, _options);
            IResponse response = formManager.ExecuteFrom(controllerName, requestQuery);

            return responseManager.GetResponseMessage(requestQuery, response);
        }
    }
}
