using Business;
using Models;
using Models.Interface;
using Security;
using System.Text.RegularExpressions;

namespace Web.Custom
{
    /// <summary>
    /// Kiểm chữ ký request và ký response cho /api/v3 (KE_HOACH_V3 4.2.3, 4.2.4).
    /// Lỗi trước khi biết khóa của user thì trả không ký; từ khi có khóa, mọi response (kể cả lỗi) đều ký
    /// </summary>
    public partial class SignatureMiddleware(RequestDelegate next, AppConfig appConfig, ISignerKeyStore keyStore, NonceStore nonceStore,
        IRevokedTokenStore revokedTokens, LoginLockout lockout, ResponseManager responseManager, TimeProvider timeProvider)
    {
        public const string UserHeader = "X-Fast-User";
        public const string TokenPath = "/api/v3/auth/token";
        public const string TimestampHeader = "X-Fast-Timestamp";
        public const string NonceHeader = "X-Fast-Nonce";
        public const string ContentSha256Header = "X-Fast-Content-SHA256";
        public const string SignatureHeader = "X-Fast-Signature";
        public const string IdempotencyKeyHeader = "Idempotency-Key";

        /// <summary>Body thô đã đọc và kiểm checksum, để tầng sau parse mà không đọc lại stream</summary>
        public const string RawBodyItem = "rawBody";
        public const string SignerItem = "signer";

        [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
        private static partial Regex NonceFormat();

        /// <summary>Route v3 cần ký</summary>
        public static bool RequiresSignature(HttpContext context)
        {
            if (!context.Request.Path.StartsWithSegments("/api/v3"))
            {
                return false;
            }
            // Không cần token, không ký: lấy giờ server để đồng bộ timestamp
            return !(HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals("/api/v3/_meta/server", StringComparison.OrdinalIgnoreCase));
        }

        public async Task Invoke(HttpContext context)
        {
            if (!RequiresSignature(context))
            {
                await next(context);
                return;
            }

            // Bước 1: xác định user và khóa. Chưa có khóa thì không ký được response
            bool isTokenRequest = IsTokenRequest(context);
            SignerKeys? signer = isTokenRequest
                ? await GetTokenRequestSignerAsync(context)
                : await GetJwtSignerAsync(context);
            if (signer == null || !signer.IsActive)
            {
                context.Items[RequestLogMiddleware.SignatureResultItem] = "UNAUTHORIZED";
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("UNAUTHORIZED"));
                return;
            }
            if (!signer.HasKey)
            {
                context.Items[RequestLogMiddleware.SignatureResultItem] = "KEY_NOT_ISSUED";
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("KEY_NOT_ISSUED"));
                return;
            }
            context.Items[SignerItem] = signer;

            string requestNonce = context.Request.Headers[NonceHeader].ToString();

            // Từ đây response luôn được ký: đệm body để băm trước khi gửi
            Stream originalBody = context.Response.Body;
            using MemoryStream buffer = new();
            context.Response.Body = buffer;
            try
            {
                IResponse? error = isTokenRequest && lockout.IsLocked(signer.UId)
                    ? responseManager.GetResponse("ACCOUNT_LOCKED")
                    : await VerifyAsync(context, signer, requestNonce);

                // Kết quả kiểm chữ ký cho log (5.2)
                context.Items[RequestLogMiddleware.SignatureResultItem] = error?.code ?? "VALID";

                if (isTokenRequest && error?.code == "INVALID_SIGNATURE")
                {
                    // Chỉ đếm chữ ký sai (dò khóa); lỗi định dạng, timestamp không tính
                    lockout.RecordFailure(signer.UId);
                }

                if (error != null)
                {
                    await ResponseHelper.WriteJsonAsync(context, error);
                }
                else
                {
                    await next(context);
                }
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            SignResponse(context, signer, requestNonce, buffer.ToArray());
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }

        public static bool IsTokenRequest(HttpContext context) =>
            HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(TokenPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// auth/token chưa có JWT: user lấy từ X-Fast-User, request phải ký bằng khóa của user đó
        /// </summary>
        private async Task<SignerKeys?> GetTokenRequestSignerAsync(HttpContext context)
        {
            string userName = context.Request.Headers[UserHeader].ToString().Trim();
            return userName.Length is 0 or > 32 ? null : await keyStore.GetAsync(userName, context.RequestAborted);
        }

        /// <summary>
        /// Route v3 khác: JWT v3 (có u_id), jti chưa thu hồi, u_id khớp tài khoản hiện tại.
        /// Token kiểu cũ (không có u_id) không dùng được cho v3
        /// </summary>
        private async Task<SignerKeys?> GetJwtSignerAsync(HttpContext context)
        {
            string userName = IpAccessMiddleware.GetUserName(context.User);
            string? uIdClaim = context.User.FindFirst("u_id")?.Value;
            string? jti = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;

            if (userName == "anonymous" || !int.TryParse(uIdClaim, out int uId) || string.IsNullOrEmpty(jti))
            {
                return null;
            }
            if (await revokedTokens.IsRevokedAsync(jti, context.RequestAborted))
            {
                return null;
            }

            SignerKeys? signer = await keyStore.GetAsync(userName, context.RequestAborted);
            return signer?.UId == uId ? signer : null;
        }

        private async Task<IResponse?> VerifyAsync(HttpContext context, SignerKeys signer, string nonce)
        {
            var headers = context.Request.Headers;
            string timestampText = headers[TimestampHeader].ToString();
            string contentSha256 = headers[ContentSha256Header].ToString().Trim();
            string signature = headers[SignatureHeader].ToString();

            if (!long.TryParse(timestampText, out long timestamp) || !NonceFormat().IsMatch(nonce) || contentSha256.Length == 0 || signature.Length == 0)
            {
                return responseManager.GetResponse("MISSING_SIGNATURE");
            }

            // Bước 2: timestamp trong ClockSkewSeconds
            long now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (Math.Abs(now - timestamp) > appConfig.clockSkewSeconds)
            {
                return responseManager.GetResponse("TIMESTAMP_EXPIRED");
            }

            // Bước 3: đọc body một lần (có giới hạn), tự băm rồi so checksum
            byte[]? body = await ReadBodyAsync(context.Request, appConfig.maxRequestBodyBytes, context.RequestAborted);
            if (body == null)
            {
                return responseManager.GetResponse("PAYLOAD_TOO_LARGE");
            }
            context.Items[RawBodyItem] = body;
            context.Request.Body = new MemoryStream(body, writable: false);

            string actualSha256 = MessageSignature.Sha256Hex(body);
            if (!MessageSignature.SignatureEquals(actualSha256, contentSha256))
            {
                return responseManager.GetResponse("CHECKSUM_MISMATCH");
            }

            // Bước 4: dựng canonicalRequest, so HMAC theo thời gian hằng
            string canonical = MessageSignature.CanonicalRequest(
                context.Request.Method,
                context.Request.PathBase.Add(context.Request.Path).Value ?? "",
                MessageSignature.CanonicalQuery(context.Request.QueryString.Value),
                timestamp,
                nonce,
                headers[IdempotencyKeyHeader].ToString(),
                actualSha256);

            if (!MessageSignature.SignatureEquals(MessageSignature.HmacHex(signer.RequestKey!, canonical), signature))
            {
                return responseManager.GetResponse("INVALID_SIGNATURE");
            }

            // Nonce ghi sau khi chữ ký đúng: request giả không đốt được nonce của đối tác
            if (!nonceStore.TryUse(signer.UId, nonce, TimeSpan.FromSeconds(2 * appConfig.clockSkewSeconds)))
            {
                return responseManager.GetResponse("NONCE_REUSED");
            }

            return null;
        }

        private void SignResponse(HttpContext context, SignerKeys signer, string requestNonce, byte[] body)
        {
            long timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            string requestId = context.Items["requestID"]?.ToString() ?? context.TraceIdentifier;
            string contentSha256 = MessageSignature.Sha256Hex(body);

            string canonical = MessageSignature.CanonicalResponse(context.Response.StatusCode, requestId, requestNonce, timestamp, contentSha256);

            context.Response.Headers[TimestampHeader] = timestamp.ToString();
            context.Response.Headers[ContentSha256Header] = contentSha256;
            context.Response.Headers[SignatureHeader] = MessageSignature.HmacHex(signer.ResponseKey!, canonical);
            context.Response.ContentLength = body.Length;
        }

        /// <returns>null nếu body vượt giới hạn</returns>
        private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
        {
            if (request.ContentLength > limit)
            {
                return null;
            }

            using MemoryStream memory = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (memory.Length + read > limit)
                {
                    return null;
                }
                memory.Write(chunk, 0, read);
            }
            return memory.ToArray();
        }
    }
}
