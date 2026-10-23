using System.Security.Cryptography;
using System.Text;

namespace Security
{
    /// <summary>
    /// Mã hóa restApiUser.private_key bằng AES-256-GCM với khóa chủ Security:KeyEncryptionKey (32 byte, base64).
    /// Bản mã gắn với u_id (AAD) nên chép private_key từ dòng này sang dòng khác sẽ không giải được.
    /// Định dạng lưu: "v1:" + base64(nonce 12 byte | tag 16 byte | bản mã)
    /// </summary>
    public class PrivateKeyProtector
    {
        public const int PrivateKeySize = 32;
        private const string Version = "v1:";
        private const int NonceSize = 12, TagSize = 16;

        private readonly byte[] _keyEncryptionKey;

        public PrivateKeyProtector(string keyEncryptionKeyBase64)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(keyEncryptionKeyBase64 ?? "");
            }
            catch (FormatException)
            {
                throw new ArgumentException("Security:KeyEncryptionKey phải là base64 của 32 byte");
            }
            if (key.Length != 32)
            {
                throw new ArgumentException("Security:KeyEncryptionKey phải là base64 của 32 byte");
            }
            _keyEncryptionKey = key;
        }

        /// <summary>private_key mới: 32 byte ngẫu nhiên</summary>
        public static byte[] GeneratePrivateKey() => RandomNumberGenerator.GetBytes(PrivateKeySize);

        /// <summary>Khóa chủ mới cho Security:KeyEncryptionKey (setup sinh lần cài đầu)</summary>
        public static string GenerateKeyEncryptionKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        public string Protect(byte[] privateKey, int uId)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] tag = new byte[TagSize];
            byte[] cipher = new byte[privateKey.Length];

            using (AesGcm aes = new(_keyEncryptionKey, TagSize))
            {
                aes.Encrypt(nonce, privateKey, cipher, tag, Aad(uId));
            }

            return Version + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
        }

        /// <summary>
        /// Giải private_key. Sai khóa chủ, sai u_id hoặc bản mã bị sửa đều ném CryptographicException
        /// </summary>
        public byte[] Unprotect(string protectedKey, int uId)
        {
            if (!protectedKey.StartsWith(Version, StringComparison.Ordinal))
            {
                throw new CryptographicException("private_key không đúng định dạng v1");
            }

            byte[] data = Convert.FromBase64String(protectedKey[Version.Length..]);
            if (data.Length <= NonceSize + TagSize)
            {
                throw new CryptographicException("private_key không đúng định dạng v1");
            }

            byte[] plain = new byte[data.Length - NonceSize - TagSize];
            using (AesGcm aes = new(_keyEncryptionKey, TagSize))
            {
                aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain, Aad(uId));
            }
            return plain;
        }

        private static byte[] Aad(int uId) => Encoding.ASCII.GetBytes($"restApiUser.private_key:{uId}");
    }
}
