using DataAccess;
using Models;
using Models.Request;
using Security;

namespace Web.Legacy
{
    /// <summary>
    /// Engine v2 cũ: đăng nhập POST api/auth/{userName} bằng GenerateAuthKey (MD5 + positions), cấp JWT audience = IP.
    /// v3 đăng nhập qua /api/auth/token (HMAC) hoặc /api/auth/portal (RSA)
    /// </summary>
    public class AuthManager(AppConfig appConfig, UserRepository userRepository, JwtTokenService jwtTokenService)
    {
        /// <returns>JWT; null nếu xác thực thất bại</returns>
        public string? AuthenticateUser(RequestAuth request)
        {
            User? user = userRepository.GetUser(request.userName);

            if (user == null)
            {
                return null;
            }

            if (request.key == EncryptionHelper.GenerateAuthKey(user.Name, request.time, user.Password, appConfig.passwordKey, appConfig.positions.Split(',')))
            {
                return jwtTokenService.GenerateJWTToken(user.Name, user.NameErp, user.IsAdmin, request.ipAddress);
            }

            return null;
        }
    }
}
