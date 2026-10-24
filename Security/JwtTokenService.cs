using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Security
{
    public class JwtTokenService
    {
        private string _secretKey, _issuer;

        public JwtTokenService(string secretKey, string issuer)
        {
            _secretKey = secretKey;
            _issuer = issuer;
        }

        /// <summary>
        /// Audience của JWT v3. Token v3 không gắn IP (đối tác hay đổi IP); IP kiểm riêng theo restApiAllowIp
        /// </summary>
        public const string Audience = "fast-api";

        /// <summary>
        /// JWT v3 (KE_HOACH 4.2.5): sub = restApiUser.name, u_id = restApiUser.id, user_id = userinfo2.id, name_erp, jti
        /// </summary>
        public string GenerateToken(string userName, int uId, int userId, string nameErp, string jti, DateTime expiresUtc, string scope = "api")
        {
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userName),
                new Claim("u_id", uId.ToString(), ClaimValueTypes.Integer32),
                new Claim("user_id", userId.ToString(), ClaimValueTypes.Integer32),
                new Claim("name_erp", nameErp),
                new Claim("scope", scope),
                new Claim(JwtRegisteredClaimNames.Jti, jti)
            };

            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: Audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: expiresUtc,
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey)), SecurityAlgorithms.HmacSha256)
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Kiểm chữ ký, issuer, audience (fast-api) và hạn. Route tự đòi claim u_id
        /// </summary>
        /// <returns>null nếu token sai chữ ký, sai issuer/audience hoặc hết hạn</returns>
        public ClaimsPrincipal? ValidateJWTToken(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.UTF8.GetBytes(_secretKey);

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = _issuer,
                    ValidAudience = Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(key)
                };

                // Validate the token
                var principal = tokenHandler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
                return principal;
            }
            catch
            {
                // Return null if token is invalid
                return null;
            }
        }
    }
}
