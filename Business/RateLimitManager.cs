using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Interface;
using Models.Options;

namespace Business
{
    public class RateLimitManager
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;
        private IMemoryCache _cache;

        private const string MEMORYCACHE_RATE = "RequestCount_{0}";
        public string memoryCacheRate {get => MEMORYCACHE_RATE; }

        public RateLimitManager(AppConfig appConfig, Options options, IMemoryCache cache)
        {
            _appConfig = appConfig;
            _options = options;
            _cache = cache;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="clientIp"></param>
        /// <param name="userName"></param>
        /// <returns>1 - Cho phép, 2 - Vượt giới hạn, 3 - Khóa ip</returns>
        public IResponse IsRequestAllowed(string clientIp, string userName)
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options, _cache);

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
            var cacheKey = string.Format(MEMORYCACHE_RATE, clientIp);

            if (_cache.TryGetValue(cacheKey, out int requestCount))
            {
                if (requestCount > _appConfig.maxRequestsPerMinute)
                {
                    return true;
                }
                else
                {
                    _cache.Set(cacheKey, requestCount + 1, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                    });

                    return false;
                }
            }
            else
            {
                // Chưa có bản ghi, nghĩa là lần đầu tiên truy cập trong phút này
                _cache.Set(cacheKey, 1, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                });
                
                return false;
            }
        }
    }
}
