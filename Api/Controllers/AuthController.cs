using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Models;
using Models.Request;
using System.Collections.Concurrent;
using Web.Custom;
using Web.Legacy;

namespace Web.Controllers
{
    /// <summary>
    /// Engine v2 cũ: POST api/auth/{userName} (không ký HMAC, xem SignatureMiddleware.IsLegacyController)
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController(AppConfig appConfig, LegacyResponseManager responseManager, AuthManager authManager) : ControllerBase
    {
        // Thay Dictionary bằng ConcurrentDictionary
        private static readonly ConcurrentDictionary<string, long> _lastReceivedTimes = new();

        //POST api/auth/login
        [HttpPost("{userName}")]
        public IActionResult Login(string userName)
        {
            var request = RequestHelper.GetRequest(Request);

            Dictionary<string, string> info = request["info"];
            Dictionary<string, string> headers = request["headers"];

            string ipAddress = info["ip"];
            RequestAuth requestAuth = new(ipAddress, userName);

            // Get values from the headers
            string oTime = headers["time"];
            string oKey = headers["key"];
            
            if (!StringValues.IsNullOrEmpty(oTime))
            {
                if (long.TryParse(oTime, out long parsedTime))
                {
                    requestAuth.time = parsedTime;
                }
                else
                {
                    // Nếu không thể phân tích chuỗi thành long, trả về lỗi
                    return ResponseHelper.ToActionResult(this, responseManager.GetResponse("INVALID_TIME_NUMBER"));
                }
            }
            if (!StringValues.IsNullOrEmpty(oKey))
            {
                requestAuth.key = oKey;
            }

            // Validate required headers
            if (requestAuth.time == 0 || string.IsNullOrEmpty(requestAuth.key))
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("MISSING_PARAMETER"));
            }

            // Check time
            _lastReceivedTimes.AddOrUpdate(
                requestAuth.userName,
                requestAuth.time, // Nếu chưa có thì thêm mới
                (userName, lastTime) =>
                {
                    if (requestAuth.time <= lastTime)
                    {
                        // Không cập nhật, trả về lastTime cũ để giữ nguyên giá trị
                        ResponseHelper.ToActionResult(this, responseManager.GetResponse("INVALID_TIME_GREATER"));
                        return lastTime;
                    }
                    // Cập nhật giá trị mới
                    return requestAuth.time;
                }
            );

            // Kiểm tra lại nếu giá trị không được cập nhật do time <= lastTime
            if (_lastReceivedTimes[requestAuth.userName] != requestAuth.time)
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("INVALID_TIME_GREATER"));
            }

            string? token = authManager.AuthenticateUser(requestAuth);

            if (string.IsNullOrEmpty(token))
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("UNAUTHORIZED"));
            }
            else
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("SUCCESS", "Bearer", appConfig.expiryToken, token));
            }
        }
    }
}
