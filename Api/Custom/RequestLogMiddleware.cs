using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Business;
using Business.Logging;
using Models;

namespace Web.Custom
{
    /// <summary>
    /// Ghi log mọi request vào hàng đợi (KE_HOACH_V3 5.2), không gọi SQL trên luồng request: LogWorker ghi theo lô.
    /// Thay LogMiddleware (ghi restApiLogData đồng bộ). Không ghi header Authorization, X-Fast-Signature; body response
    /// chứa JWT (auth/token, api/auth) không lưu
    /// </summary>
    public class RequestLogMiddleware(RequestDelegate next, RequestLogQueue queue, AppConfig appConfig)
    {
        public const string FormItem = "logForm";
        public const string SignatureResultItem = "signatureResult";

        /// <summary>Route trả JWT trong body</summary>
        private static readonly string[] TokenPaths = ["/api/v3/auth/token", "/api/auth"];

        public async Task Invoke(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            DateTime startedAt = DateTime.Now;

            // Đọc body trước rồi tua lại cho tầng sau: tầng sau có thể đóng stream (using StreamReader) nên không đọc lại sau được
            byte[] requestBody = await ReadRequestBodyAsync(context.Request);

            Stream originalBody = context.Response.Body;
            using MemoryStream responseBuffer = new();
            context.Response.Body = responseBuffer;
            try
            {
                await next(context);
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            byte[] responseBody = responseBuffer.ToArray();
            await originalBody.WriteAsync(responseBody, context.RequestAborted);
            stopwatch.Stop();

            try
            {
                queue.Enqueue(BuildEntry(context, startedAt, (int)stopwatch.ElapsedMilliseconds, requestBody, responseBody));
            }
            catch (Exception)
            {
                // Log hỏng không được làm hỏng response đã gửi
            }
        }

        private RequestLogEntry BuildEntry(HttpContext context, DateTime startedAt, int durationMs, byte[] requestBody, byte[] responseBody)
        {
            HttpRequest request = context.Request;
            bool isToken = TokenPaths.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

            var signer = context.Items[SignatureMiddleware.SignerItem] as SignerKeys;
            string? userName = signer?.Name ?? NullIfAnonymous(IpAccessMiddleware.GetUserName(context.User));
            if (userName == null && isToken)
            {
                userName = request.Headers[SignatureMiddleware.UserHeader].ToString() is { Length: > 0 } header ? header : null;
            }

            string? resultCode = ResultCode(context.Response.ContentType, responseBody);
            (byte[]? keptRequest, string? requestSha) = Keep(requestBody);
            (byte[]? keptResponse, string? responseSha) = isToken ? (null, null) : Keep(responseBody);

            return new RequestLogEntry
            {
                ReqId = context.Items["requestID"] is Guid id ? id : Guid.NewGuid(),
                CreatedAt = startedAt,
                UId = signer?.UId ?? (int.TryParse(context.User.FindFirst("u_id")?.Value, out int uId) ? uId : null),
                UserName = userName,
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                Method = request.Method,
                Path = request.Path.Value ?? "",
                QueryString = request.QueryString.HasValue ? request.QueryString.Value : null,
                Form = context.Items[FormItem] as string,
                StatusCode = context.Response.StatusCode,
                ResultCode = resultCode,
                DurationMs = durationMs,
                RequestBytes = requestBody.Length,
                ResponseBytes = responseBody.Length,
                RequestBody = keptRequest,
                RequestSha256 = requestSha,
                ResponseBody = keptResponse,
                ResponseSha256 = responseSha,
                ContentSha256 = Header(request, SignatureMiddleware.ContentSha256Header),
                Nonce = Header(request, SignatureMiddleware.NonceHeader),
                ClientTimestamp = long.TryParse(request.Headers[SignatureMiddleware.TimestampHeader], out long ts) ? ts : null,
                SignatureResult = context.Items[SignatureResultItem] as string,
                IdempotencyKey = Header(request, SignatureMiddleware.IdempotencyKeyHeader),
                IsAttack = resultCode != null && AttackCodes.All.Contains(resultCode)
            };
        }

        /// <summary>Body nhỏ giữ nguyên để worker nén; lớn hơn logMaxBodyBytes chỉ giữ SHA256</summary>
        private (byte[]? Body, string? Sha) Keep(byte[] body)
        {
            if (body.Length == 0)
            {
                return (null, null);
            }
            return body.Length <= appConfig.logMaxBodyBytes ? (body, null) : (null, Convert.ToHexStringLower(SHA256.HashData(body)));
        }

        /// <summary>
        /// Body vượt maxRequestBodyBytes thì không đọc (SignatureMiddleware sẽ trả PAYLOAD_TOO_LARGE), chỉ ghi số byte khai báo
        /// </summary>
        private async Task<byte[]> ReadRequestBodyAsync(HttpRequest request)
        {
            if (request.ContentLength == 0 || request.ContentLength > appConfig.maxRequestBodyBytes)
            {
                return [];
            }

            request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: appConfig.maxRequestBodyBytes);
            using MemoryStream memory = new();
            try
            {
                await request.Body.CopyToAsync(memory, request.HttpContext.RequestAborted);
            }
            catch (IOException)
            {
                // Vượt bufferLimit (không khai Content-Length): bỏ body log, tầng sau tự báo lỗi
                return [];
            }
            finally
            {
                if (request.Body.CanSeek)
                {
                    request.Body.Position = 0;
                }
            }
            return memory.ToArray();
        }

        /// <summary>Thuộc tính "code" cấp gốc của response JSON (mọi response v3 và response lỗi đều có)</summary>
        public static string? ResultCode(string? contentType, byte[] body)
        {
            if (body.Length == 0 || contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }
            try
            {
                var reader = new Utf8JsonReader(body);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return null;
                }
                while (reader.Read() && reader.CurrentDepth >= 1)
                {
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                    {
                        bool isCode = reader.ValueTextEquals("code");
                        reader.Read();
                        if (isCode && reader.TokenType == JsonTokenType.String)
                        {
                            return reader.GetString();
                        }
                        reader.Skip();
                    }
                }
            }
            catch (JsonException)
            {
            }
            return null;
        }

        private static string? Header(HttpRequest request, string name) => request.Headers[name].ToString() is { Length: > 0 } value ? value : null;

        private static string? NullIfAnonymous(string userName) => userName == "anonymous" ? null : userName;
    }
}
