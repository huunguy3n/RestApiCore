using DataAccess;
using Models;
using Models.Request;
using Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Business
{
    public class AuthManager
    {
        private readonly AppConfig _appConfig;

        public AuthManager(AppConfig appConfig)
        {
            _appConfig = appConfig;
        }

        /// <summary>
        /// Xác thực user
        /// </summary>
        /// <param name="name"></param>
        /// <param name="password">Plain text</param>
        /// <param name="secretKey"></param>
        /// <returns>Nếu trống xác thực thất bại</returns>
        public string AuthenticateUser(RequestAuth request)
        {
            UserRepository userRepository = new UserRepository(_appConfig);
            User user = userRepository.GetUser(request.userName);

            if (user == null)
            {
                return null;
            }

            if (request.key == EncryptionHelper.GenerateAuthKey(user.Name, request.time, user.Password, _appConfig.secretKey, _appConfig.positions.Split(',')))
            {
                JwtTokenService jwtTokenService = new JwtTokenService(_appConfig.secretKey, _appConfig.expiryToken, _appConfig.issuerToken);

                return jwtTokenService.GenerateJWTToken(user.Name, user.NameErp, user.IsAdmin, request.ipAddress);
            }

            return null;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="jwtToken"></param>
        /// <param name="userName"></param>
        /// <returns></returns>
        public ClaimsPrincipal AuthenticateToken(string jwtToken, string ipAddress)
        {
            JwtTokenService jwtTokenService = new JwtTokenService(_appConfig.secretKey, _appConfig.expiryToken, _appConfig.issuerToken);
            return jwtTokenService.ValidateJWTToken(jwtToken, ipAddress);
        }
    }
}
