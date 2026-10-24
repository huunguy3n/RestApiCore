using Business;
using Models;
using Models.Interface;
using Security;
using System.Text.Json;

namespace Web.Custom
{
    /// <summary>
    /// Điều phối chữ ký cho /api (KE_HOACH 4.2.3, 4.2.4): route nào phải ký, xác định user và khóa (body auth/token hoặc JWT + thu hồi),
    /// chính sách JWT portal, đếm khóa đăng nhập, đọc body có giới hạn, đệm và ký response. Kiểm nonce/HMAC/phát lại và tính chữ ký
    /// response ở MessageAuthenticator. Lỗi trước khi biết khóa của user thì trả không ký; từ khi có khóa, mọi response (kể cả lỗi) đều ký
    /// </summary>
    public class SignatureMiddleware(RequestDelegate next, AppConfig appConfig, ISignerKeyStore keyStore, IRevokedTokenStore revokedTokens,
        LoginLockout lockout, ResponseManager responseManager, MessageAuthenticator authenticator)
    {
        public const string TokenPath = "/api/auth/token";
        public const string NonceHeader = "X-Fast-Nonce";
        public const string SignatureHeader = "X-Fast-Signature";
        public const string IdempotencyKeyHeader = "Idempotency-Key";

        /// <summary>Body thô đã đọc và kiểm checksum, để tầng sau parse mà không đọc lại stream</summary>
        public const string RawBodyItem = "rawBody";
        public const string SignerItem = "signer";

        /// <summary>Route cần ký</summary>
        public static bool RequiresSignature(HttpContext context)
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                return false;
            }
            // Không cần token, không ký: giờ server + khóa công khai portal; đăng nhập portal (mật khẩu mã hóa RSA, không có private_key)
            return !(HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals("/api/_meta/server", StringComparison.OrdinalIgnoreCase)
                || HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(PortalEndpoints.PortalPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// JWT scope=portal chỉ đọc GET /api/_meta/* (trừ ping, dùng để thử ký); không ký request/response vì trình duyệt không giữ private_key
        /// </summary>
        public static bool PortalMayCall(HttpContext context) =>
            HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/_meta", StringComparison.OrdinalIgnoreCase)
            && !context.Request.Path.StartsWithSegments("/api/_meta/ping", StringComparison.OrdinalIgnoreCase);

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

            if (!isTokenRequest && context.User.FindFirst("scope")?.Value == "portal")
            {
                if (!PortalMayCall(context))
                {
                    context.Items[RequestLogMiddleware.SignatureResultItem] = "PORTAL_TOKEN_NOT_ALLOWED";
                    await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("PORTAL_TOKEN_NOT_ALLOWED"));
                    return;
                }
                context.Items[SignerItem] = signer;
                context.Items[RequestLogMiddleware.SignatureResultItem] = "PORTAL";
                await next(context);
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
        /// auth/token chưa có JWT: user lấy từ userName trong body, request phải ký bằng khóa của user đó.
        /// Body được đọc ở đây và để lại trong Items cho bước kiểm chữ ký
        /// </summary>
        private async Task<SignerKeys?> GetTokenRequestSignerAsync(HttpContext context)
        {
            byte[]? body = await ReadBodyAsync(context.Request, appConfig.maxRequestBodyBytes, context.RequestAborted);
            if (body == null)
            {
                return null;
            }
            context.Items[RawBodyItem] = body;

            string userName = ReadUserName(body)?.Trim() ?? "";
            return userName.Length is 0 or > 32 ? null : await keyStore.GetAsync(userName, context.RequestAborted);
        }

        private static string? ReadUserName(byte[] body)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                return json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("userName", out JsonElement userName)
                    && userName.ValueKind == JsonValueKind.String
                    ? userName.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
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

        /// <summary>
        /// Thứ tự: nonce + độ lệch giờ → đọc body (giới hạn maxRequestBodyBytes) → HMAC + nonce dùng một lần
        /// </summary>
        private async Task<IResponse?> VerifyAsync(HttpContext context, SignerKeys signer, string nonce)
        {
            string signature = context.Request.Headers[SignatureHeader].ToString();

            if (authenticator.CheckFreshness(nonce, signature) is string stale)
            {
                return responseManager.GetResponse(stale);
            }

            // Đọc body một lần (có giới hạn); auth/token đã đọc ở bước 1
            byte[]? body = context.Items[RawBodyItem] as byte[]
                ?? await ReadBodyAsync(context.Request, appConfig.maxRequestBodyBytes, context.RequestAborted);
            if (body == null)
            {
                return responseManager.GetResponse("PAYLOAD_TOO_LARGE");
            }
            context.Items[RawBodyItem] = body;
            context.Request.Body = new MemoryStream(body, writable: false);

            return authenticator.VerifySignature(signer, body, nonce, signature) is string invalid
                ? responseManager.GetResponse(invalid)
                : null;
        }

        private void SignResponse(HttpContext context, SignerKeys signer, string requestNonce, byte[] body)
        {
            context.Response.Headers[SignatureHeader] = authenticator.SignResponse(signer, requestNonce, body);
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
