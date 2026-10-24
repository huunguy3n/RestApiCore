using System.Security.Cryptography;
using System.Text;

namespace Security
{
    /// <summary>
    /// Băm mật khẩu tài khoản portal (portalAccount.password_hash): PBKDF2-HMAC-SHA256, salt ngẫu nhiên 16 byte,
    /// định dạng "v1.&lt;vòng lặp&gt;.&lt;salt base64&gt;.&lt;hash base64&gt;". Băm một chiều (khác restApiUser.password là AES giải ngược được)
    /// </summary>
    public static class PortalPasswordHasher
    {
        private const string Version = "v1";
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        public const int DefaultIterations = 310_000;

        public static string Hash(string password, int iterations = DefaultIterations)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
            return $"{Version}.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        /// <summary>So theo thời gian hằng; chuỗi băm hỏng hoặc sai dạng trả false</summary>
        public static bool Verify(string password, string? stored)
        {
            if (!TryParse(stored, out int iterations, out byte[] salt, out byte[] expected))
            {
                return false;
            }
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        /// <summary>
        /// Băm giả để thời gian xử lý tài khoản không tồn tại ≈ tài khoản có thật (chống dò tên đăng nhập qua thời gian)
        /// </summary>
        public static void BurnTime(string password) => Verify(password, DummyHash);

        private static readonly string DummyHash = Hash("khong-co-tai-khoan");

        private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
        {
            iterations = 0;
            salt = hash = [];
            string[] parts = (stored ?? "").Split('.');
            if (parts.Length != 4 || parts[0] != Version || !int.TryParse(parts[1], out iterations) || iterations is < 10_000 or > 5_000_000)
            {
                return false;
            }
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                hash = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }
            return salt.Length >= 8 && hash.Length >= 16;
        }
    }
}
