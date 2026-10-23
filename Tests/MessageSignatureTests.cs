using System.Security.Cryptography;
using System.Text;
using Security;

namespace Tests
{
    /// <summary>
    /// Vector thử cố định của chữ ký v3 (KE_HOACH_V3 4.2). Giá trị mong đợi tính độc lập bằng Python hmac/hashlib,
    /// dùng chung cho code mẫu trên portal: đổi một giá trị ở đây là đổi giao thức với đối tác
    /// </summary>
    public class MessageSignatureTests
    {
        // private_key = byte 0x00..0x1f
        internal const string PrivateKeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        internal const string Body = "{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}";
        internal const string BodySha256 = "534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f";
        internal const string Nonce = "9b2e4c1f0a7d4e3b";
        internal const string IdempotencyKey = "6f1c2d3e-0000-4000-8000-000000000001";
        internal const string RequestSignature = "6728c67bb7eded95a6b13b56c3bcb13378c521fe55b0f23b97a3581918adfa54";

        internal const string ResponseBody = "{\"code\":\"SUCCESS\"}";
        internal const string RequestId = "8c2f0000-0000-4000-8000-000000000002";
        internal const string ResponseSignature = "1972c2ae0b53f37dd745becadfd19669a8da4d968fb282891ed43fddec17fd68";

        private static byte[] PrivateKey => Convert.FromBase64String(PrivateKeyBase64);

        [Fact]
        public void DeriveKeys_KhopVector()
        {
            Assert.Equal("97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f", Convert.ToHexStringLower(MessageSignature.DeriveRequestKey(PrivateKey)));
            Assert.Equal("62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd", Convert.ToHexStringLower(MessageSignature.DeriveResponseKey(PrivateKey)));
        }

        [Fact]
        public void Sha256Hex_KhopVector()
        {
            Assert.Equal(BodySha256, MessageSignature.Sha256Hex(Encoding.UTF8.GetBytes(Body)));
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", MessageSignature.Sha256Hex([]));
        }

        [Fact]
        public void RequestSignature_KhopVector()
        {
            string canonical = MessageSignature.CanonicalRequest("post", "/API/v3/Sale-Invoice", MessageSignature.CanonicalQuery("?unitCode=CTY&dryRun=false"),
                1792574400, Nonce, IdempotencyKey, BodySha256);

            Assert.Equal(RequestSignature, MessageSignature.HmacHex(MessageSignature.DeriveRequestKey(PrivateKey), canonical));
        }

        [Fact]
        public void ResponseSignature_KhopVector()
        {
            string canonical = MessageSignature.CanonicalResponse(200, RequestId, Nonce, 1792574401, MessageSignature.Sha256Hex(Encoding.UTF8.GetBytes(ResponseBody)));

            Assert.Equal(ResponseSignature, MessageSignature.HmacHex(MessageSignature.DeriveResponseKey(PrivateKey), canonical));
        }

        [Fact]
        public void ChuKyRequest_KhongDungLamChuKyResponse()
        {
            string canonical = MessageSignature.CanonicalResponse(200, RequestId, Nonce, 1792574401, MessageSignature.Sha256Hex(Encoding.UTF8.GetBytes(ResponseBody)));

            // Ký bằng K_request (khóa của client) ra chữ ký khác, client không giả được response
            string forged = MessageSignature.HmacHex(MessageSignature.DeriveRequestKey(PrivateKey), canonical);
            Assert.Equal("1b2180546cede2586822fdb2dad04d418c004e50c83fdfc869f93290bebbee0b", forged);
            Assert.False(MessageSignature.SignatureEquals(ResponseSignature, forged));
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("?", "")]
        [InlineData("unitCode=CTY&dryRun=false", "dryRun=false&unitCode=CTY")]
        [InlineData("b=2&a=1&a=0&c", "a=0&a=1&b=2&c=")]
        [InlineData("name=Nguy%E1%BB%85n+V%C4%83n", "name=Nguy%E1%BB%85n%20V%C4%83n")]
        [InlineData("name=Nguy%e1%bb%85n%20V%c4%83n", "name=Nguy%E1%BB%85n%20V%C4%83n")]
        [InlineData("x=a%2Bb&y=~-._", "x=a%2Bb&y=~-._")]
        [InlineData("Z=1&a=1", "Z=1&a=1")]
        public void CanonicalQuery_ChuanHoa(string? raw, string expected)
        {
            Assert.Equal(expected, MessageSignature.CanonicalQuery(raw));
        }

        [Fact]
        public void SuaMotByteBody_ChuKySai()
        {
            byte[] tampered = Encoding.UTF8.GetBytes(Body.Replace("\"Quantity\":2", "\"Quantity\":3"));
            string canonical = MessageSignature.CanonicalRequest("POST", "/api/v3/sale-invoice", "dryRun=false&unitCode=CTY",
                1792574400, Nonce, IdempotencyKey, MessageSignature.Sha256Hex(tampered));

            Assert.False(MessageSignature.SignatureEquals(RequestSignature, MessageSignature.HmacHex(MessageSignature.DeriveRequestKey(PrivateKey), canonical)));
        }

        [Fact]
        public void DoiQuery_ChuKySai()
        {
            string canonical = MessageSignature.CanonicalRequest("POST", "/api/v3/sale-invoice", MessageSignature.CanonicalQuery("unitCode=CTY&dryRun=true"),
                1792574400, Nonce, IdempotencyKey, BodySha256);

            Assert.False(MessageSignature.SignatureEquals(RequestSignature, MessageSignature.HmacHex(MessageSignature.DeriveRequestKey(PrivateKey), canonical)));
        }

        [Theory]
        [InlineData(RequestSignature, true)]
        [InlineData("6728C67BB7EDED95A6B13B56C3BCB13378C521FE55B0F23B97A3581918ADFA54", true)]
        [InlineData("6728c67bb7eded95a6b13b56c3bcb13378c521fe55b0f23b97a3581918adfa55", false)]
        [InlineData("6728c67b", false)]
        [InlineData(null, false)]
        public void SignatureEquals_ThoiGianHang(string? actual, bool expected)
        {
            Assert.Equal(expected, MessageSignature.SignatureEquals(RequestSignature, actual));
        }
    }

    public class PrivateKeyProtectorTests
    {
        private static readonly string Kek = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

        [Fact]
        public void ProtectUnprotect_KhoiPhucDungKhoa()
        {
            var protector = new PrivateKeyProtector(Kek);
            byte[] key = PrivateKeyProtector.GeneratePrivateKey();

            string stored = protector.Protect(key, 7);

            Assert.StartsWith("v1:", stored);
            Assert.Equal(key, protector.Unprotect(stored, 7));
            Assert.True(stored.Length <= 512, "vừa cột restApiUser.private_key nvarchar(512)");
        }

        [Fact]
        public void Unprotect_SaiUId_SaiKhoaChu_HoacBiSua_DeuLoi()
        {
            var protector = new PrivateKeyProtector(Kek);
            string stored = protector.Protect(PrivateKeyProtector.GeneratePrivateKey(), 7);

            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(stored, 8));
            Assert.ThrowsAny<CryptographicException>(() => new PrivateKeyProtector(PrivateKeyProtector.GenerateKeyEncryptionKey()).Unprotect(stored, 7));

            byte[] raw = Convert.FromBase64String(stored[3..]);
            raw[^1] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v1:" + Convert.ToBase64String(raw), 7));
        }

        [Theory]
        [InlineData("")]
        [InlineData("khong-phai-base64")]
        [InlineData("AAECAw==")]
        public void KhoaChuKhongHopLe_BaoLoi(string kek)
        {
            Assert.Throws<ArgumentException>(() => new PrivateKeyProtector(kek));
        }
    }
}
