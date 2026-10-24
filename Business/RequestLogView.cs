using System.Text.Json.Nodes;

namespace Business
{
    public sealed record LogFilter(DateTime From, DateTime To, string? UserName, string? Form, int? StatusCode, Guid? ReqId);

    public sealed record LogRequestSummary(Guid ReqId, DateTime CreatedAt, string? UserName, string? IpAddress, string Method, string Path, string? Form,
        int StatusCode, string? ResultCode, int DurationMs, int RequestBytes, int ResponseBytes, string? SignatureResult);

    public sealed record LogPage(int Total, IReadOnlyList<LogRequestSummary> Items);

    /// <summary>Body đã giải nén (null = không lưu hoặc đã bị dọn); chưa lọc bí mật</summary>
    public sealed record LogRequestDetail(LogRequestSummary Summary, string? QueryString, byte[]? RequestBody, byte[]? ResponseBody);

    /// <summary>Số liệu hiệu suất theo form trong một khoảng thời gian (restApiLogReq gần đây + restApiLogAgg đã dọn)</summary>
    public sealed record FormPerformance(string Form, int Requests, int Errors, long TotalMs, int MaxMs, int Lt100, int Lt500, int Lt2000, int Ge2000)
    {
        public double AverageMs => Requests == 0 ? 0 : (double)TotalMs / Requests;
    }

    /// <summary>Đọc restApiLogReq / restApiLogBody / restApiLogAgg (DataAccess.LogViewRepository). Chỉ đọc</summary>
    public interface IRequestLogReader
    {
        Task<LogPage> ListAsync(LogFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

        /// <param name="maxBodyBytes">Giải nén tối đa chừng này byte mỗi body</param>
        Task<LogRequestDetail?> GetAsync(Guid reqId, int maxBodyBytes, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<FormPerformance>> PerformanceAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
    }

    /// <summary>Che bí mật trong body log trước khi hiển thị (WebPortal, PortalApp)</summary>
    public static class LogRedaction
    {
        public const int MaxBodyBytes = 256 * 1024;
        public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

        private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "passwd", "secret", "token", "accessToken", "access_token", "credential", "privateKey", "private_key", "authorization", "signature", "apiKey", "api_key"
        };

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
                return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch (System.Text.Json.JsonException)
            {
                return "[body không phải JSON, không hiển thị]";
            }
        }

        /// <summary>Chỉ giữ ký tự tên đăng nhập/form hợp lệ</summary>
        public static string? Clean(string? value, int max)
        {
            string clean = new((value ?? "").Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@' or '_').ToArray());
            return clean.Length == 0 ? null : clean.Length > max ? clean[..max] : clean;
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
    }
}
