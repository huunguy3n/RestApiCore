using DataAccess;
using Models;

namespace Business.V3
{
    public interface IPortalCredentialStore
    {
        /// <summary>restApiUser.password đã mã hóa (AES, passwordKey); null nếu không có hoặc tài khoản ngừng</summary>
        Task<string?> GetEncryptedPasswordAsync(string userName, CancellationToken cancellationToken = default);
    }

    public class PortalCredentialStore(AppConfig appConfig) : IPortalCredentialStore
    {
        public Task<string?> GetEncryptedPasswordAsync(string userName, CancellationToken cancellationToken = default) =>
            new UserRepository(appConfig).GetEncryptedPasswordAsync(userName, cancellationToken);
    }
}
