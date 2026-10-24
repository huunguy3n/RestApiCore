using System.Text.Json.Nodes;
using Models;

namespace Business
{
    public sealed record LogFilter(DateTime From, DateTime To, string? UserName, string? Form, int? StatusCode, Guid? ReqId);

    public sealed record LogRequestSummary(Guid ReqId, DateTime CreatedAt, string? UserName, string? IpAddress, string Method, string Path, string? Form,
        int StatusCode, string? ResultCode, int DurationMs, int RequestBytes, int ResponseBytes, string? SignatureResult);

    public sealed record LogPage(int Total, IReadOnlyList<LogRequestSummary> Items);

    /// <summary>Body đã giải nén (null = không lưu hoặc đã bị dọn); chưa lọc bí mật</summary>
    public sealed record LogRequestDetail(LogRequestSummary Summary, string? QueryString, byte[]? RequestBody, byte[]? ResponseBody);

    /// <summary>Đọc restApiLogReq / restApiLogBody (DataAccess.LogViewRepository). Chỉ đọc</summary>
    public interface IRequestLogReader
    {
        Task<LogPage> ListAsync(LogFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

        /// <param name="maxBodyBytes">Giải nén tối đa chừng này byte mỗi body</param>
        Task<LogRequestDetail?> GetAsync(Guid reqId, int maxBodyBytes, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Xem log request/response của dự án trên portal. Metadata cần logs.read; body (dữ liệu nghiệp vụ của khách) cần logs.body.read
    /// riêng, mỗi lần mở body ghi audit. Body được che các khóa bí mật và chỉ hiện nếu là JSON
    /// </summary>
    public sealed class RequestLogViewService(IRequestLogReader reader, IPortalAccessRepository repository, PortalAuthorizationService authorization, TimeProvider time)
    {
        public const int MaxPageSize = 100;
        public const int MaxBodyBytes = 256 * 1024;
        public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

        private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "passwd", "secret", "token", "accessToken", "access_token", "credential", "privateKey", "private_key", "authorization", "signature", "apiKey", "api_key"
        };

        public async Task<ServiceResult> ListAsync(int actorUId, string actorName, DateTime? from, DateTime? to, string? userName, string? form, int? statusCode, string? reqId,
            int page, int pageSize, string? requestId, CancellationToken cancellationToken = default)
        {
            PortalAccess actor = await authorization.GetAccessAsync(actorUId, cancellationToken);
            if (!actor.Has(PortalPermissions.LogsRead))
            {
                await repository.WriteAuditAsync(new AuditEvent(actorUId, actorName, "logs.list", "log", "*", "DENIED", requestId), cancellationToken);
                return new ServiceResult("FORBIDDEN");
            }

            DateTime end = to ?? time.GetLocalNow().DateTime;
            DateTime start = from ?? end.AddDays(-1);
            if (start >= end || end - start > MaxRange)
            {
                return new ServiceResult("INVALID_SHAPE");
            }
            Guid? parsedReq = null;
            if (!string.IsNullOrWhiteSpace(reqId))
            {
                if (!Guid.TryParse(reqId, out Guid g))
                {
                    return new ServiceResult("INVALID_SHAPE");
                }
                parsedReq = g;
            }

            LogFilter filter = new(start, end, Clean(userName, 32), Clean(form, 32), statusCode, parsedReq);
            return new ServiceResult("SUCCESS", await reader.ListAsync(filter, Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), cancellationToken));
        }

        public async Task<ServiceResult> GetAsync(int actorUId, string actorName, string reqId, string? requestId, CancellationToken cancellationToken = default)
        {
            PortalAccess actor = await authorization.GetAccessAsync(actorUId, cancellationToken);
            if (!actor.Has(PortalPermissions.LogsRead))
            {
                await repository.WriteAuditAsync(new AuditEvent(actorUId, actorName, "logs.get", "log", reqId, "DENIED", requestId), cancellationToken);
                return new ServiceResult("FORBIDDEN");
            }
            if (!Guid.TryParse(reqId, out Guid id))
            {
                return new ServiceResult("INVALID_SHAPE");
            }

            LogRequestDetail? detail = await reader.GetAsync(id, MaxBodyBytes, cancellationToken);
            if (detail == null)
            {
                return new ServiceResult("NOT_FOUND");
            }

            bool canReadBody = actor.Has(PortalPermissions.LogsBodyRead);
            if (canReadBody)
            {
                // Mở body là truy cập dữ liệu nghiệp vụ của khách: luôn ghi vết
                await repository.WriteAuditAsync(new AuditEvent(actorUId, actorName, "logs.body.read", "log", id.ToString(), "SUCCESS", requestId), cancellationToken);
            }

            return new ServiceResult("SUCCESS", new
            {
                summary = detail.Summary,
                queryString = detail.QueryString,
                bodiesVisible = canReadBody,
                requestBody = canReadBody ? Redact(detail.RequestBody) : null,
                responseBody = canReadBody ? Redact(detail.ResponseBody) : null
            });
        }

        /// <summary>Che giá trị của các khóa bí mật ở mọi cấp; body không phải JSON thì không hiển thị</summary>
        public static string? Redact(byte[]? body)
        {
            if (body == null || body.Length == 0)
            {
                return null;
            }
            try
            {
                JsonNode? node = JsonNode.Parse(body);
                if (node == null)
                {
                    return null;
                }
                Walk(node);
                return node.ToJsonString();
            }
            catch (System.Text.Json.JsonException)
            {
                return "[body không phải JSON, không hiển thị]";
            }
        }

        private static void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    if (SecretKeys.Contains(key))
                    {
                        obj[key] = "***";
                    }
                    else if (obj[key] is JsonNode child)
                    {
                        Walk(child);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item != null)
                    {
                        Walk(item);
                    }
                }
            }
        }

        /// <summary>Chỉ giữ ký tự tên đăng nhập/form hợp lệ</summary>
        private static string? Clean(string? value, int max)
        {
            string clean = new((value ?? "").Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@' or '_').ToArray());
            return clean.Length == 0 ? null : clean.Length > max ? clean[..max] : clean;
        }
    }
}
