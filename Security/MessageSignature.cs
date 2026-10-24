using System.Security.Cryptography;
using System.Text;

namespace Security
{
    /// <summary>
    /// Ký và kiểm thông điệp v3 (KE_HOACH mục 4.2): HMAC-SHA256 hai chiều (request và response cùng ký md5(body)+nonce, khác khóa).
    /// Phần thuần, không phụ thuộc ASP.NET, dùng chung cho server, client mẫu và vector thử
    /// </summary>
    public static class MessageSignature
    {
        private static readonly byte[] RequestLabel = Encoding.ASCII.GetBytes("FAST-API-V3-REQUEST");
        private static readonly byte[] ResponseLabel = Encoding.ASCII.GetBytes("FAST-API-V3-RESPONSE");

        /// <summary>K_request = HMAC-SHA256(private_key, "FAST-API-V3-REQUEST")</summary>
        public static byte[] DeriveRequestKey(byte[] privateKey) => HMACSHA256.HashData(privateKey, RequestLabel);

        /// <summary>K_response = HMAC-SHA256(private_key, "FAST-API-V3-RESPONSE")</summary>
        public static byte[] DeriveResponseKey(byte[] privateKey) => HMACSHA256.HashData(privateKey, ResponseLabel);

        /// <summary>Hex chữ thường của SHA256(byte thô); body rỗng = SHA256 của chuỗi rỗng</summary>
        public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

        public static string HmacHex(byte[] key, string message) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

        /// <summary>
        /// So hai chữ ký hex theo thời gian hằng, không phân biệt hoa thường
        /// </summary>
        public static bool SignatureEquals(string expectedHex, string? actualHex)
        {
            if (actualHex == null)
            {
                return false;
            }
            byte[] expected = Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant());
            byte[] actual = Encoding.ASCII.GetBytes(actualHex.Trim().ToLowerInvariant());
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        /// <summary>Hex chữ thường của MD5(byte thô); body rỗng = d41d8cd98f00b204e9800998ecf8427e</summary>
        public static string Md5Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));

        /// <summary>
        /// Thông điệp ký: md5Hex(body) + nonce. Request ký bằng K_request với nonce của request;
        /// response ký bằng K_response với nonce của request tương ứng (không phát lại được cho request khác)
        /// </summary>
        public static string Message(ReadOnlySpan<byte> body, string nonce) => Md5Hex(body) + nonce;

        /// <summary>
        /// Nonce có dạng "&lt;unix giây&gt;-&lt;chuỗi ngẫu nhiên&gt;"; trả giây ở đầu nonce, sai dạng trả false
        /// </summary>
        public static bool TryParseNonce(string nonce, out long unixSeconds)
        {
            unixSeconds = 0;
            int dash = nonce.IndexOf('-');
            return dash is >= 9 and <= 12 && nonce.Length - dash - 1 is >= 8 and <= 48
                && nonce.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
                && long.TryParse(nonce.AsSpan(0, dash), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out unixSeconds);
        }
    }
}