using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Security
{
    /// <summary>
    /// Cặp khóa RSA giải mật khẩu đăng nhập portal (KE_HOACH 5.3). Trình duyệt mã hóa bằng khóa công khai (RSA-OAEP SHA-256,
    /// công bố qua /_meta/server dạng JWK), server giải bằng khóa riêng. Một cặp cho cả instance.
    /// Nguồn khóa riêng: tệp PFX (Security:PortalKeyPath + PortalKeyPassword) hoặc Windows Certificate Store (Security:PortalKeyThumbprint);
    /// không khai gì thì sinh khóa tạm trong bộ nhớ (chỉ dùng cho máy dev: restart là đổi khóa)
    /// </summary>
    public sealed class PortalKeyProvider : IDisposable
    {
        private readonly RSA _rsa;

        public string Kid { get; }
        /// <summary>true khi dùng khóa tạm sinh lúc khởi động</summary>
        public bool IsEphemeral { get; }

        private PortalKeyProvider(RSA rsa, bool ephemeral)
        {
            if (rsa.KeySize < 2048)
            {
                throw new ArgumentException("Khóa RSA portal phải từ 2048 bit");
            }
            _rsa = rsa;
            IsEphemeral = ephemeral;
            Kid = Thumbprint(rsa.ExportParameters(false));
        }

        public static PortalKeyProvider FromPfx(string path, string? password)
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
            return new PortalKeyProvider(certificate.GetRSAPrivateKey() ?? throw new ArgumentException($"{path} không có khóa riêng RSA"), false);
        }

        public static PortalKeyProvider FromStore(string thumbprint)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            X509Certificate2 certificate = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).FirstOrDefault()
                ?? throw new ArgumentException($"Không có chứng chỉ {thumbprint} trong LocalMachine\\My");
            return new PortalKeyProvider(certificate.GetRSAPrivateKey() ?? throw new ArgumentException($"Chứng chỉ {thumbprint} không có khóa riêng RSA"), false);
        }

        public static PortalKeyProvider Ephemeral() => new(RSA.Create(2048), true);

        /// <summary>JWK khóa công khai (RFC 7517) để trình duyệt nhập vào WebCrypto</summary>
        public object PublicJwk()
        {
            RSAParameters parameters = _rsa.ExportParameters(false);
            return new
            {
                kty = "RSA",
                n = Base64Url(parameters.Modulus!),
                e = Base64Url(parameters.Exponent!),
                alg = "RSA-OAEP-256",
                use = "enc",
                kid = Kid
            };
        }

        /// <returns>null nếu bản mã sai (khóa khác, bị sửa, không phải base64)</returns>
        public byte[]? Decrypt(string base64Cipher)
        {
            try
            {
                return _rsa.Decrypt(Convert.FromBase64String(base64Cipher), RSAEncryptionPadding.OaepSHA256);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// kid = JWK thumbprint RFC 7638: base64url(SHA256('{"e":…,"kty":"RSA","n":…}'))
        /// </summary>
        public static string Thumbprint(RSAParameters parameters)
        {
            string canonical = $"{{\"e\":\"{Base64Url(parameters.Exponent!)}\",\"kty\":\"RSA\",\"n\":\"{Base64Url(parameters.Modulus!)}\"}}";
            return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Mã hóa như trình duyệt (dùng cho test và client mẫu)</summary>
        public byte[] EncryptForTest(byte[] data) => _rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);

        public void Dispose() => _rsa.Dispose();
    }
}
