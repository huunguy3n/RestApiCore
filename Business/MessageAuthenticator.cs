using Models;
using Security;

namespace Business
{
    /// <summary>
    /// Giao thức HMAC v3 (KE_HOACH 4.2.3, 4.2.4), không phụ thuộc HTTP:
    /// request ký HMAC-SHA256(K_request, md5Hex(body) + nonce), nonce "&lt;unix giây&gt;-&lt;ngẫu nhiên&gt;" trong clockSkewSeconds,
    /// mỗi nonce dùng một lần theo u_id trong 2 × clockSkewSeconds (NonceStore, in-process); response ký HMAC-SHA256(K_response, md5Hex(body) + nonce của request).
    /// SignatureMiddleware đọc header/body và gọi theo thứ tự: CheckFreshness → đọc body → VerifySignature
    /// </summary>
    public sealed class MessageAuthenticator(AppConfig appConfig, NonceStore nonceStore, TimeProvider timeProvider)
    {
        /// <summary>
        /// Có chữ ký, nonce đúng dạng, giây trong nonce lệch giờ server không quá clockSkewSeconds.
        /// </summary>
        /// <returns>null nếu đạt; MISSING_SIGNATURE hoặc TIMESTAMP_EXPIRED</returns>
        public string? CheckFreshness(string nonce, string signature)
        {
            if (!MessageSignature.TryParseNonce(nonce, out long nonceTime) || signature.Length == 0)
            {
                return "MISSING_SIGNATURE";
            }

            long now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (Math.Abs(now - nonceTime) > appConfig.clockSkewSeconds)
            {
                return "TIMESTAMP_EXPIRED";
            }
            return null;
        }

        /// <summary>
        /// Chữ ký trên byte thô của body, so theo thời gian hằng. Nonce chỉ ghi sau khi chữ ký đúng:
        /// request giả không đốt được nonce của đối tác
        /// </summary>
        /// <returns>null nếu đạt; INVALID_SIGNATURE hoặc NONCE_REUSED</returns>
        public string? VerifySignature(SignerKeys signer, ReadOnlySpan<byte> body, string nonce, string signature)
        {
            string expected = MessageSignature.HmacHex(signer.RequestKey!, MessageSignature.Message(body, nonce));
            if (!MessageSignature.SignatureEquals(expected, signature))
            {
                return "INVALID_SIGNATURE";
            }

            if (!nonceStore.TryUse(signer.UId, nonce, NonceStore.ReplayWindow(appConfig.clockSkewSeconds)))
            {
                return "NONCE_REUSED";
            }
            return null;
        }

        /// <summary>
        /// Chữ ký response (X-Fast-Signature): cùng công thức request nhưng khóa K_response và nonce của request,
        /// nên response không phát lại được cho request khác
        /// </summary>
        public string SignResponse(SignerKeys signer, string requestNonce, ReadOnlySpan<byte> body) =>
            MessageSignature.HmacHex(signer.ResponseKey!, MessageSignature.Message(body, requestNonce));
    }
}
