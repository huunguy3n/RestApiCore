using System.Security.Cryptography;
using System.Text;

namespace Security
{
    /// <summary>
    /// Ký và kiểm thông điệp v3 (KE_HOACH mục 4.2): SHA-256 checksum body, HMAC-SHA256 hai chiều.
    /// Phần thuần, không phụ thuộc ASP.NET, dùng chung cho server, client mẫu và vector thử
    /// </summary>
    public static class MessageSignature
    {
        public const string RequestAlgorithm = "FAST-HMAC-SHA256";
        public const string ResponseAlgorithm = "FAST-HMAC-SHA256-RESPONSE";

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

        /// <summary>
        /// canonicalQuery: giải mã query thô, encode lại tên và giá trị theo RFC 3986, sắp theo tên rồi theo giá trị (ordinal), nối bằng &amp;.
        /// Không có query thì chuỗi rỗng. Tham số không có giá trị viết "ten="
        /// </summary>
        public static string CanonicalQuery(string? rawQuery)
        {
            if (string.IsNullOrEmpty(rawQuery))
            {
                return string.Empty;
            }

            string query = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;

            var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    int index = part.IndexOf('=');
                    string name = index < 0 ? part : part[..index];
                    string value = index < 0 ? string.Empty : part[(index + 1)..];
                    return (Name: Rfc3986(FormDecode(name)), Value: Rfc3986(FormDecode(value)));
                })
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal);

            return string.Join("&", pairs.Select(p => $"{p.Name}={p.Value}"));
        }

        public static string CanonicalRequest(string method, string path, string canonicalQuery, long timestamp, string nonce, string? idempotencyKey, string contentSha256)
        {
            return string.Join("\n",
                RequestAlgorithm,
                method.ToUpperInvariant(),
                path.ToLowerInvariant(),
                canonicalQuery,
                timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                nonce,
                idempotencyKey ?? string.Empty,
                contentSha256.ToLowerInvariant());
        }

        public static string CanonicalResponse(int statusCode, string requestId, string requestNonce, long timestamp, string contentSha256)
        {
            return string.Join("\n",
                ResponseAlgorithm,
                statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                requestId,
                requestNonce,
                timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contentSha256.ToLowerInvariant());
        }

        /// <summary>
        /// Encode RFC 3986: giữ A-Z a-z 0-9 - . _ ~, còn lại %XX (hex hoa) trên byte UTF-8
        /// </summary>
        public static string Rfc3986(string value)
        {
            StringBuilder sb = new();
            foreach (byte b in Encoding.UTF8.GetBytes(value))
            {
                char c = (char)b;
                if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('%').Append(b.ToString("X2"));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Giải mã như ASP.NET đọc query: '+' là dấu cách, %XX theo UTF-8
        /// </summary>
        private static string FormDecode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
