using System.Security.Cryptography;
using System.Text;
using Security;

namespace Tests
{
    /// <summary>
    /// Vector thử cố định của chữ ký v3 (KE_HOACH 4.2). Giá trị mong đợi tính độc lập bằng Python hmac/hashlib,
    /// dùng chung cho code mẫu trên portal: đổi một giá trị ở đây là đổi giao thức với đối tác
    /// </summary>
    public class MessageSignatureTests
    {
        // private_key = byte 0x00..0x1f
        internal const string PrivateKeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        internal const string Body = "{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}";
        internal const string BodyMd5 = "8e0a0e9704e5282159769a91441af571";
        internal const string BodySha256 = "534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f";
        internal const string Nonce = "1792574400-9b2e4c1f0a7d4e3b";
        internal const string RequestSignature = "9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac";

        internal const string ResponseBody = "{\"code\":\"SUCCESS\"}";
        internal const string RequestId = "8c2f0000-0000-4000-8000-000000000002";
        internal const string ResponseSignature = "ad5353907e543ef9429cfe159bdaf1ded0ae2c37a27208110bbc2a47ef902bac";

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
        public void Md5Hex_KhopVector()
        {
            Assert.Equal(BodyMd5, MessageSignature.Md5Hex(Encoding.UTF8.GetBytes(Body)));
            Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", MessageSignature.Md5Hex([]));
        }

        [Fact]
        public void RequestSignature_KhopVector()
        {
            string message = MessageSignature.RequestMessage(Encoding.UTF8.GetBytes(Body), Nonce);

            Assert.Equal(BodyMd5 + Nonce, message);
            Assert.Equal(RequestSignature, MessageSignature.HmacHex(MessageSignature.DeriveRequestKey(PrivateKey), message));
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
            Assert.Equal("40d2309d1cbd8c5f30a800c90d4609f9df10809f8b8ee6a358b9b571316f9430", forged);
            Assert.False(MessageSignature.SignatureEquals(ResponseSignature, forged));
        }

        [Theory]
        [InlineData("1792574400-9b2e4c1f0a7d4e3b", true, 1792574400)]
        [InlineData("1792574400-ab_CD-12", true, 1792574400)]
        [InlineData("1792574400-ngan", false, 0)]
        [InlineData("9b2e4c1f0a7d4e3b", false, 0)]
        [InlineData("1792574400dinhkem0001", false, 0)]
        [InlineData("abc1234567-9b2e4c1f0a7d4e3b", false, 0)]
        [InlineData("1792574400-co dau cach", false, 0)]
        [InlineData("-9b2e4c1f0a7d4e3b", false, 0)]
        public void TryParseNonce_ChiNhanDangGiayNgauNhien(string nonce, bool expected, long seconds)
        {
            Assert.Equal(expected, MessageSignature.TryParseNonce(nonce, out long actual));
            Assert.Equal(seconds, actual);
        }

        [Fact]
        public void SuaMotByteBody_ChuKySai()
        {
            byte[] tampered = Encoding.UTF8.GetBytes(Body.Replace("\"Quantity\":2", "\"Quantity\":3"));
            string message = MessageSignature.RequestMessage(tampered, Nonce);

            Assert.False(MessageSignature.SignatureEquals(RequestSignature, MessageSignature.HmacHex(MessageSignature.DeriveRequestKey(PrivateKey), message)));
        }

        [Theory]
        [InlineData(RequestSignature, true)]
        [InlineData("9F79234564E12B262C72B20339C00B520874F00EFE9678CF5BE7052C920339AC", true)]
        [InlineData("9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ad", false)]
        [InlineData("9f792345", false)]
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
