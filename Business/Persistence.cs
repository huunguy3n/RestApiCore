using Models;

namespace Business
{
    // Cổng lưu trữ mà tầng nghiệp vụ cần. Business không tham chiếu DataAccess/SqlClient:
    // DataAccess cài đặt các interface này, Api đăng ký DI. Interface hẹp theo đúng nhu cầu của lớp gọi.
    // Repository chỉ Api dùng (LogRepository, ColumnSchemaRepository) không cần cổng ở đây

    /// <summary>restApi_GetUserRights: tài khoản, khóa, quyền form của một restApiUser.name</summary>
    public interface IUserRightsRepository
    {
        /// <summary>Null nếu không có tài khoản; còn hiệu lực hay không xem UserRights.IsActive</summary>
        Task<UserRights?> GetUserRightsAsync(string userName, CancellationToken cancellationToken = default);
    }

    public interface IPortalCredentialStore
    {
        /// <summary>restApiUser.password đã mã hóa (AES, passwordKey); null nếu không có hoặc tài khoản ngừng</summary>
        Task<string?> GetEncryptedPasswordAsync(string userName, CancellationToken cancellationToken = default);
    }

    /// <summary>restApiRevokedToken: JWT thu hồi theo jti</summary>
    public interface IRevokedTokenRepository
    {
        Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default);

        /// <param name="expiresAt">Giờ địa phương máy SQL (so với GETDATE())</param>
        Task RevokeAsync(string jti, int uId, DateTime expiresAt, CancellationToken cancellationToken = default);
    }

    /// <summary>restApi_CheckIPAddress: restApiAllowIp + restApiBlackListIp</summary>
    public interface IIpAccessRepository
    {
        Task<bool> CheckIPAddressAsync(string clientIp, string userName, CancellationToken cancellationToken = default);
    }

    /// <summary>restApi_CheckUnitRights (dmdvcs + ff_CheckUnitRight)</summary>
    public interface IUnitRightsRepository
    {
        /// <summary>SUCCESS, UNIT_NOT_FOUND hoặc UNIT_DENIED</summary>
        Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default);
    }
}
