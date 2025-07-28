using Business;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Models;
using Models.Options;
using Models.Request;
using System.Collections.Concurrent;
using Web.Custom;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;
        // Thay Dictionary bằng ConcurrentDictionary
        private static ConcurrentDictionary<string, long> _lastReceivedTimes = new();

        public AuthController(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }

        //POST api/auth/login
        [HttpPost("{userName}")]
        public IActionResult Login(string userName)
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);

            string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW";
            RequestAuth requestAuth = new RequestAuth(ipAddress, userName);

            // Get values from the headers
            Request.Headers.TryGetValue("time", out var oTime);
            Request.Headers.TryGetValue("key", out var oKey);

            if (!StringValues.IsNullOrEmpty(oTime))
            {
                if (long.TryParse(oTime.FirstOrDefault() ?? "", out long parsedTime))
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
                requestAuth.key = oKey.FirstOrDefault() ?? "";
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

            AuthManager authManager = new AuthManager(_appConfig);
            string token = authManager.AuthenticateUser(requestAuth);

            if (string.IsNullOrEmpty(token))
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("UNAUTHORIZED"));
            }
            else
            {
                return ResponseHelper.ToActionResult(this, responseManager.GetResponse("SUCCESS", "Bearer", _appConfig.expiryToken, token));
            }
        }
    }
}
