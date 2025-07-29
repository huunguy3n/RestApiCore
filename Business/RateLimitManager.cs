using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Interface;

namespace Business
{
    public class RateLimitManager(AppConfig appConfig, IMemoryCache cache, ResponseManager responseManager)
    {
        private const string MEMORYCACHE_RATE = "RequestCount_{0}";
        public string memoryCacheRate {get => MEMORYCACHE_RATE; }


        /// <summary>
        /// Kiểm tra xem IP và tên người dùng có được phép truy cập hay không
        /// </summary>
        /// <param name="clientIp"></param>
        /// <param name="userName"></param>
        /// <returns>1 - Cho phép, 2 - Vượt giới hạn, 3 - Khóa ip</returns>
        public IResponse IsRequestAllowed(string clientIp, string userName)
        {
            if (CheckLimitRequest(clientIp))
            {
                return responseManager.GetResponse("TOO_MANY_REQUESTS");
            }
            else
            {
                ServerRepository serverRepository = new ServerRepository(appConfig);

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

            if (cache.TryGetValue(cacheKey, out int requestCount))
            {
                if (requestCount > appConfig.maxRequestsPerMinute)
                {
                    return true;
                }
                else
                {
                    cache.Set(cacheKey, requestCount + 1, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                    });

                    return false;
                }
            }
            else
            {
                // Chưa có bản ghi, nghĩa là lần đầu tiên truy cập trong phút này
                cache.Set(cacheKey, 1, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                });
                
                return false;
            }
        }
    }
}
