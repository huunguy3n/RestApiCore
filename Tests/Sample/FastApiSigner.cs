using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Tests.Sample
{
    /// <summary>
    /// Client mẫu C# cho đối tác: ký request và kiểm chữ ký response FastAPI v3 (HMAC-SHA256 hai chiều).
    /// Tự chứa, chỉ dùng System.Security.Cryptography; không tham chiếu code server để test là một bản cài đặt độc lập
    /// </summary>
    public sealed class FastApiSigner(byte[] privateKey)
    {
        private readonly byte[] _requestKey = HMACSHA256.HashData(privateKey, Encoding.ASCII.GetBytes("FAST-API-V3-REQUEST"));
        private readonly byte[] _responseKey = HMACSHA256.HashData(privateKey, Encoding.ASCII.GetBytes("FAST-API-V3-RESPONSE"));

        /// <summary>
        /// Gắn X-Fast-Nonce ("unixGiây-ngẫu nhiên") và X-Fast-Signature = HMAC-SHA256(K_request, md5Hex(body) + nonce).
        /// Trả nonce để kiểm response
        /// </summary>
        public string Sign(HttpRequestMessage request, byte[] body, long unixTime, string? nonce = null, string? idempotencyKey = null)
        {
            nonce ??= unixTime + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            string message = Convert.ToHexStringLower(MD5.HashData(body)) + nonce;

            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Fast-Nonce", nonce);
            request.Headers.Add("X-Fast-Signature", Hmac(_requestKey, message));
            if (idempotencyKey != null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }
            return nonce;
        }

        /// <summary>
        /// Response không ký, ký sai, hoặc không khớp nonce của request thì không được tin nội dung
        /// </summary>
        public bool VerifyResponse(HttpResponseMessage response, byte[] body, string requestNonce)
        {
            string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

            string? timestamp = Header("X-Fast-Timestamp"), signature = Header("X-Fast-Signature"), requestId = Header("X-Request-ID");
            string contentSha256 = Convert.ToHexStringLower(SHA256.HashData(body));
            if (timestamp == null || signature == null || requestId == null || Header("X-Fast-Content-SHA256") != contentSha256)
            {
                return false;
            }

            string canonical = string.Join("\n",
                "FAST-HMAC-SHA256-RESPONSE",
                ((int)response.StatusCode).ToString(),
                requestId,
                requestNonce,
                timestamp,
                contentSha256);

            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hmac(_responseKey, canonical)), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
        }

        private static string Hmac(byte[] key, string message) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
    }
}
