using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Security
{
    public class JwtTokenService
    {
        private string _secretKey, _issuer;
        private int _expiryDuration;

        public JwtTokenService(string secretKey, int expiryDurationInMinutes, string issuer)
        {
            _secretKey = secretKey;
            _expiryDuration = expiryDurationInMinutes;
            _issuer = issuer;
        }

        public string GenerateJWTToken(string userName, string userERP, bool isAdmin, string ipAddress)
        {
            // Step 1: Define the claims for the token (e.g., user information)
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userName),
                new Claim("userERP", userERP),
                new Claim(ClaimTypes.Role, isAdmin ? "Admin" : "User"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            // Step 2: Generate the key and credentials for signing the token
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            // Step 3: Create the token with claims, expiration, and signing credentials
            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: ipAddress,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_expiryDuration),
                signingCredentials: creds
            );

            // Step 4: Return the token as a string
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Audience của JWT v3. Token v3 không gắn IP (đối tác hay đổi IP); IP kiểm riêng theo restApiAllowIp
        /// </summary>
        public const string V3Audience = "fast-api-v3";

        /// <summary>
        /// JWT v3 (KE_HOACH_V3 4.2.5): sub = restApiUser.name, u_id = restApiUser.id, user_id = userinfo2.id, name_erp, jti
        /// </summary>
        public string GenerateV3Token(string userName, int uId, int userId, string nameErp, string jti, DateTime expiresUtc, string scope = "api")
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
                audience: V3Audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: expiresUtc,
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey)), SecurityAlgorithms.HmacSha256)
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Nhận token kiểu cũ (audience = IP) và token v3 (audience = fast-api-v3). Route v3 tự đòi claim u_id
        /// </summary>
        public ClaimsPrincipal ValidateJWTToken(string token, string ipAddress)
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
                    ValidAudiences = [ipAddress, V3Audience],
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
