using Microsoft.Extensions.Caching.Hybrid;
using Models;
using Models.Interface;

namespace Business
{
    public interface IIpAccessManager
    {
        Task<IResponse> CheckAsync(string clientIp, string userName, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Kiểm IP theo restApi_CheckIPAddress, kết quả cache HybridCache để không gọi proc ở mọi request.
    /// Đếm số request đã chuyển sang ASP.NET Core Rate Limiting (Program.cs)
    /// </summary>
    public class IpAccessManager(AppConfig appConfig, HybridCache cache, ResponseManager responseManager, IIpAccessRepository repository) : IIpAccessManager
    {
        public const string CacheTag = "ip-access";

        public async Task<IResponse> CheckAsync(string clientIp, string userName, CancellationToken cancellationToken = default)
        {
            bool allowed = await cache.GetOrCreateAsync(
                $"ip-access:{userName}:{clientIp}",
                async token => await repository.CheckIPAddressAsync(clientIp, userName, token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(appConfig.ipCheckCacheSeconds) },
                [CacheTag],
                cancellationToken);

            return responseManager.GetResponse(allowed ? "SUCCESS" : "ACCESS_DENIED");
        }

        /// <summary>
        /// Gọi khi đổi restApiAllowIp / restApiBlackListIp để có hiệu lực ngay
        /// </summary>
        public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
        {
            return cache.RemoveByTagAsync(CacheTag, cancellationToken);
        }
    }
}
