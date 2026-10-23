using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Hybrid;
using Models;
using System.Data;

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
    public class UnitRightsChecker(AppConfig appConfig, HybridCache cache) : IUnitRightsChecker
    {
        public async Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default)
        {
            // dmdvcs.ma_dvcs char(8): dài hơn chắc chắn không có
            if (unitCode.Length > 8)
            {
                return "UNIT_NOT_FOUND";
            }

            return await cache.GetOrCreateAsync(
                $"unit-rights:{userId}:{unitCode.ToUpperInvariant()}:{action}",
                async token =>
                {
                    var ds = await Provider.Provider.RetrieveAsync(appConfig.appConnectionString, "exec restApi_CheckUnitRights @UserID, @UnitCode, @Action", token,
                        new SqlParameter("@UserID", SqlDbType.Int) { Value = userId },
                        new SqlParameter("@UnitCode", SqlDbType.VarChar, 33) { Value = unitCode },
                        new SqlParameter("@Action", SqlDbType.VarChar, 5) { Value = action });
                    return ds.Tables[0].Rows[0]["code"].ToString()!.Trim();
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(appConfig.userCacheSeconds) },
                cancellationToken: cancellationToken);
        }
    }
}
