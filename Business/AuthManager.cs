using DataAccess;
using Models;
using Models.Request;
using Security;
using System.Security.Claims;

namespace Business
{
    public class AuthManager(AppConfig appConfig)
    {

        /// <summary>
        /// Xác thực user
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Nếu trống xác thực thất bại</returns>
        public string AuthenticateUser(RequestAuth request)
        {
            UserRepository userRepository = new UserRepository(appConfig);
            User user = userRepository.GetUser(request.userName);

            if (user == null)
            {
                return null;
            }

            if (request.key == EncryptionHelper.GenerateAuthKey(user.Name, request.time, user.Password, appConfig.secretKey, appConfig.positions.Split(',')))
            {
                JwtTokenService jwtTokenService = new JwtTokenService(appConfig.secretKey, appConfig.expiryToken, appConfig.issuerToken);

                return jwtTokenService.GenerateJWTToken(user.Name, user.NameErp, user.IsAdmin, request.ipAddress);
            }

            return null;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="jwtToken"></param>
        /// <param name="ipAddress"></param>
        /// <returns></returns>
        public ClaimsPrincipal AuthenticateToken(string jwtToken, string ipAddress)
        {
            JwtTokenService jwtTokenService = new JwtTokenService(appConfig.secretKey, appConfig.expiryToken, appConfig.issuerToken);
            return jwtTokenService.ValidateJWTToken(jwtToken, ipAddress);
        }
    }
}
