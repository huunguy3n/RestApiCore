using DataAccess;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Models;

namespace Business
{
    public interface IRevokedTokenStore
    {
        Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default);
        Task RevokeAsync(string jti, int uId, DateTime expiresAtUtc, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// JWT thu hồi: lưu SQL (restApiRevokedToken), tra qua HybridCache. Thu hồi ghi đè cache ngay nên có hiệu lực tức thì
    /// trên instance này (baseline một instance)
    /// </summary>
    public class RevokedTokenStore(AppConfig appConfig, HybridCache cache) : IRevokedTokenStore
    {
        public async Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default)
        {
            return await cache.GetOrCreateAsync(
                Key(jti),
                async token => await new RevokedTokenRepository(appConfig).IsRevokedAsync(jti, token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(appConfig.userCacheSeconds) },
                cancellationToken: cancellationToken);
        }

        public async Task RevokeAsync(string jti, int uId, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
        {
            // restApiRevokedToken.expires_at so với GETDATE() (giờ máy SQL) nên lưu giờ địa phương
            await new RevokedTokenRepository(appConfig).RevokeAsync(jti, uId, expiresAtUtc.ToLocalTime(), cancellationToken);
            await cache.SetAsync(Key(jti), true, new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(appConfig.userCacheSeconds) }, cancellationToken: cancellationToken);
        }

        private static string Key(string jti) => $"revoked:{jti}";
    }

    /// <summary>
    /// Khóa đăng nhập (auth/token) sau loginMaxFailures lần chữ ký sai liên tiếp trong loginLockMinutes. In-process
    /// </summary>
    public class LoginLockout(AppConfig appConfig, IMemoryCache cache)
    {
        private readonly object _lock = new();

        public bool IsLocked(int uId) => cache.TryGetValue(Key(uId), out int failures) && failures >= appConfig.loginMaxFailures;

        public void RecordFailure(int uId)
        {
            lock (_lock)
            {
                cache.TryGetValue(Key(uId), out int failures);
                cache.Set(Key(uId), failures + 1, TimeSpan.FromMinutes(appConfig.loginLockMinutes));
            }
        }

        public void Reset(int uId) => cache.Remove(Key(uId));

        private static string Key(int uId) => $"login-fail:{uId}";
    }
}
