using DataAccess;
using Models;
using Models.Interface;
using Models.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Business
{
    public class RateLimitManager
    {
        private ObjectCache cache = MemoryCache.Default;

        private readonly AppConfig _appConfig;
        private readonly Options _options;

        public RateLimitManager(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="clientIp"></param>
        /// <param name="userName"></param>
        /// <returns>1 - Cho phép, 2 - Vượt giới hạn, 3 - Khóa ip</returns>
        public IResponse IsRequestAllowed(string clientIp, string userName)
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);

            if (CheckLimitRequest(clientIp))
            {
                return responseManager.GetResponse("TOO_MANY_REQUESTS");
            }
            else
            {
                ServerRepository serverRepository = new ServerRepository(_appConfig);

                if (serverRepository.CheckIPAddress(clientIp, userName))
                {
                    return responseManager.GetResponse("SUCCESS");
                }
                else
                {
                    return responseManager.GetResponse("ACCESS_DENIED");
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="clientIp"></param>
        /// <returns>true - đã quá giới hạn truy cập, false - thỏa điều kiện</returns>
        private bool CheckLimitRequest(string clientIp)
        {
            var cacheKey = $"RequestCount_{clientIp}";

            if (cache.Contains(cacheKey))
            {
                var requestCount = (int)cache.Get(cacheKey);

                if (requestCount >= _appConfig.maxRequestInMinutes)
                {
                    return true;
                }
                else
                {
                    cache.Set(cacheKey, requestCount + 1, DateTimeOffset.Now.AddSeconds(60));
                    return false;
                }
            }
            else
            {
                cache.Add(cacheKey, 1, DateTimeOffset.Now.AddSeconds(60));
                return false;
            }
        }
    }
}
