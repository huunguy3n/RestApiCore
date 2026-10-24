using System.ComponentModel;
using System.Security.Cryptography;
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
    public sealed record SignerKeys(int UId, string Name, bool IsActive, byte[]? RequestKey, byte[]? ResponseKey, int? UserId = null, string NameErp = "")
    {
        public bool HasKey => RequestKey != null && ResponseKey != null;

        /// <summary>userinfo2.admin = 1</summary>
        public bool ErpAdmin { get; init; }

        /// <summary>Form được phép (restApiAllowForm). Không có dòng = mọi form</summary>
        public IReadOnlySet<string> AllowForms { get; init; } = new HashSet<string>();

        /// <summary>Admin FBO (admin = 1) hoặc không khai restApiAllowForm thì mọi form; có khai thì chỉ các form đã khai</summary>
        public bool CanAccessForm(string form) => ErpAdmin || AllowForms.Count == 0 || AllowForms.Contains(form);
    }

    public interface ISignerKeyStore
    {
        /// <summary>Null nếu không có tài khoản</summary>
        Task<SignerKeys?> GetAsync(string userName, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Tra restApi_GetUserRights, giải private_key bằng PrivateKeyProtector, cache HybridCache userCacheSeconds
    /// </summary>
    public class SignerKeyStore(AppConfig appConfig, HybridCache cache, PrivateKeyProtector protector, IUserRightsRepository users, ILogger<SignerKeyStore> logger) : ISignerKeyStore, ISignerCacheInvalidator
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
            UserRights? rights = await users.GetUserRightsAsync(userName, cancellationToken);
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

            return new SignerKeys(rights.UId, rights.Name, rights.IsActive, requestKey, responseKey, rights.UserId, rights.NameErp)
            {
                ErpAdmin = rights.ErpAdmin,
                AllowForms = new HashSet<string>(rights.AllowForms, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
