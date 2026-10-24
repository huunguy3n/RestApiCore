using Microsoft.Extensions.Caching.Hybrid;
using Models;

namespace Business
{
    public interface IUnitRightsChecker
    {
        /// <summary>SUCCESS, UNIT_NOT_FOUND hoặc UNIT_DENIED</summary>
        Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Kiểm unitCode qua restApi_CheckUnitRights (dmdvcs + ff_CheckUnitRight, admin được mọi đơn vị), cache userCacheSeconds.
    /// unitCode đi bằng tham số, không ghép vào SQL (sửa lỗi 1.4 #2)
    /// </summary>
    public class UnitRightsChecker(AppConfig appConfig, HybridCache cache, IUnitRightsRepository repository) : IUnitRightsChecker
    {
        /// <summary>dmdvcs.ma_dvcs char(8)</summary>
        public const int MaxUnitLength = 8;

        public async Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default)
        {
            // Dài hơn cột mã đơn vị thì chắc chắn không có
            if (unitCode.Length > MaxUnitLength)
            {
                return "UNIT_NOT_FOUND";
            }

            return await cache.GetOrCreateAsync(
                $"unit-rights:{userId}:{unitCode.ToUpperInvariant()}:{action}",
                async token => await repository.CheckAsync(userId, unitCode, action, token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(appConfig.userCacheSeconds) },
                cancellationToken: cancellationToken);
        }
    }
}
