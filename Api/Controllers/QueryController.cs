using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Models.Interface;
using Models.Request;
using Security;
using Web.Custom;
using Web.Legacy;

namespace Web.Controllers
{
    /// <summary>
    /// Engine v2 cũ: POST api/query/{name}, checksum MD5, JWT audience = IP. Đối tác mới dùng route v3 (FormEndpoint)
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class QueryController(LegacyResponseManager responseManager, FormManager formManager) : ControllerBase
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
            string checksum = headers.TryGetValue("checksum", out value) ? value : "";

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

            if (string.IsNullOrEmpty(checksum))
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("MISSING_PARAMETER"));
            }

            //Checksum body
            if (EncryptionHelper.ComputeMD5Hash(requestQuery.jsonBody) != checksum)
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("INVALID_CHECKSUM"));
            }

            IResponse response = formManager.ExecuteFrom(controllerName, requestQuery);

            return ResponseHelper.ToActionResult(this, response);
        }
    }
}
