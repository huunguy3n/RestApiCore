using System.ComponentModel;
using System.Security.Cryptography;
using DataAccess;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Models;
using Security;

namespace Business
{
    /// <summary>
    /// Khóa ký của một tài khoản: K_request kiểm request, K_response ký response. Không giữ private_key gốc.
    /// Đánh dấu bất biến để HybridCache dùng lại instance, không serialize khóa
    /// </summary>
    [ImmutableObject(true)]
    public sealed record SignerKeys(int UId, string Name, bool IsActive, byte[]? RequestKey, byte[]? ResponseKey)
    {
        public bool HasKey => RequestKey != null && ResponseKey != null;
    }

    public interface ISignerKeyStore
    {
        /// <summary>Null nếu không có tài khoản</summary>
        Task<SignerKeys?> GetAsync(string userName, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Tra restApi_GetUserRights, giải private_key bằng PrivateKeyProtector, cache HybridCache userCacheSeconds
    /// </summary>
    public class SignerKeyStore(AppConfig appConfig, HybridCache cache, PrivateKeyProtector protector, ILogger<SignerKeyStore> logger) : ISignerKeyStore
    {
        public const string CacheTag = "signer-keys";

        public async Task<SignerKeys?> GetAsync(string userName, CancellationToken cancellationToken = default)
        {
            return await cache.GetOrCreateAsync(
                $"signer-keys:{userName}",
                async token => await LoadAsync(userName, token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(appConfig.userCacheSeconds) },
                [CacheTag],
                cancellationToken);
        }

        /// <summary>
        /// Gọi khi cấp/đổi private_key hoặc đổi trạng thái tài khoản để có hiệu lực ngay
        /// </summary>
        public ValueTask InvalidateAsync(CancellationToken cancellationToken = default) => cache.RemoveByTagAsync(CacheTag, cancellationToken);

        private async Task<SignerKeys?> LoadAsync(string userName, CancellationToken cancellationToken)
        {
            UserRights? rights = await new UserRepository(appConfig).GetUserRightsAsync(userName, cancellationToken);
            if (rights == null)
            {
                return null;
            }

            byte[]? requestKey = null, responseKey = null;
            if (!string.IsNullOrEmpty(rights.PrivateKey))
            {
                try
                {
                    byte[] privateKey = protector.Unprotect(rights.PrivateKey, rights.UId);
                    requestKey = MessageSignature.DeriveRequestKey(privateKey);
                    responseKey = MessageSignature.DeriveResponseKey(privateKey);
                    CryptographicOperations.ZeroMemory(privateKey);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    // Sai khóa chủ hoặc bản mã hỏng: coi như chưa cấp khóa, ghi log để quản trị cấp lại
                    logger.LogError("Không giải được private_key của u_id {UId}: {Message}", rights.UId, ex.Message);
                }
            }

            return new SignerKeys(rights.UId, rights.Name, rights.IsActive, requestKey, responseKey);
        }
    }

    /// <summary>
    /// Nonce đã dùng theo u_id, giữ 2 × clockSkewSeconds. Kiểm và ghi nguyên tử (một instance, in-process)
    /// </summary>
    public class NonceStore(Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        private readonly object _lock = new();

        /// <returns>false nếu nonce đã dùng</returns>
        public bool TryUse(int uId, string nonce, TimeSpan ttl)
        {
            string key = $"nonce:{uId}:{nonce}";
            lock (_lock)
            {
                if (cache.TryGetValue(key, out _))
                {
                    return false;
                }
                Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(cache, key, true, ttl);
                return true;
            }
        }
    }
}
