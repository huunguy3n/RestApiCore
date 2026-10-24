#if NETCOREAPP
#nullable disable
#endif
using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace FboWeb.ApiLog
{
    /// <summary>Bộ lọc danh sách đã kiểm tra: mọi giá trị đi vào SQL bằng tham số</summary>
    public sealed class ApiLogFilter
    {
        public DateTime From { get; set; }
        /// <summary>Mốc cuối (không gồm): 00:00 ngày sau "Đến ngày"</summary>
        public DateTime ToExclusive { get; set; }
        public string UserName { get; set; }
        /// <summary>Tiền tố đường dẫn đã escape cho LIKE</summary>
        public string PathPrefix { get; set; }
        /// <summary>Khoảng HTTP status (gồm hai đầu): "500" → 500..500, "4xx" → 400..499</summary>
        public int? StatusFrom { get; set; }
        public int? StatusTo { get; set; }
        public string ResultCode { get; set; }
        /// <summary>"!SUCCESS": có mã kết quả và khác giá trị này</summary>
        public string ResultNot { get; set; }
        public Guid? ReqId { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>Đọc và kiểm tra query string của AppHandler/ApiLogView.ashx. Không phụ thuộc System.Web (Tests dùng chung)</summary>
    public static class ApiLogQuery
    {
        public const int MaxPageSize = 100;
        public const int DefaultPageSize = 25;
        public const int MaxRangeDays = 31;

        /// <returns>null nếu hợp lệ, ngược lại là mã lỗi (INVALID_DATE, INVALID_RANGE, INVALID_STATUS, INVALID_REQ_ID)</returns>
        public static string TryParseList(NameValueCollection query, DateTime today, out ApiLogFilter filter)
        {
            filter = null;
            DateTime from, to;
            if (!ParseDate(query["from"], today, out from) || !ParseDate(query["to"], today, out to)) return "INVALID_DATE";
            DateTime toExclusive = to.AddDays(1);
            if (toExclusive <= from || (toExclusive - from).TotalDays > MaxRangeDays) return "INVALID_RANGE";

            int? statusFrom = null, statusTo = null;
            string statusText = (query["status"] ?? "").Trim();
            if (statusText.Length > 0)
            {
                int value;
                if (statusText.Length == 3 && statusText[0] >= '1' && statusText[0] <= '5' && string.Equals(statusText.Substring(1), "xx", StringComparison.OrdinalIgnoreCase))
                {
                    statusFrom = (statusText[0] - '0') * 100;
                    statusTo = statusFrom + 99;
                }
                else if (int.TryParse(statusText, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 100 && value <= 599)
                {
                    statusFrom = statusTo = value;
                }
                else return "INVALID_STATUS";
            }

            string resultText = (query["result"] ?? "").Trim();
            bool resultNot = resultText.StartsWith("!", StringComparison.Ordinal);
            string result = Clean(resultNot ? resultText.Substring(1) : resultText, 64, "_-");

            Guid? reqId;
            if (!TryParseReqId(query["reqId"], out reqId)) return "INVALID_REQ_ID";

            int page, pageSize;
            if (!int.TryParse(query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1) page = 1;
            if (!int.TryParse(query["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize < 1) pageSize = DefaultPageSize;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;

            filter = new ApiLogFilter
            {
                From = from,
                ToExclusive = toExclusive,
                UserName = Clean(query["user"], 32, "-._@"),
                PathPrefix = EscapeLike(Clean(query["path"], 256, "/-._~{}")),
                StatusFrom = statusFrom,
                StatusTo = statusTo,
                ResultCode = resultNot ? null : result,
                ResultNot = resultNot ? result : null,
                ReqId = reqId,
                Page = page,
                PageSize = pageSize
            };
            return null;
        }

        /// <summary>Rỗng → null (hợp lệ); có giá trị thì phải là GUID</summary>
        public static bool TryParseReqId(string text, out Guid? reqId)
        {
            reqId = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) return true;
            Guid value;
            if (!Guid.TryParse(text, out value)) return false;
            reqId = value;
            return true;
        }

        private static bool ParseDate(string text, DateTime fallback, out DateTime value)
        {
            if (string.IsNullOrWhiteSpace(text)) { value = fallback.Date; return true; }
            return DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        /// <summary>Chỉ giữ chữ-số ASCII và các ký tự cho phép, cắt ở max</summary>
        public static string Clean(string text, int max, string extra)
        {
            if (string.IsNullOrEmpty(text)) return null;
            StringBuilder sb = new StringBuilder();
            foreach (char c in text.Trim())
            {
                if (sb.Length >= max) break;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || extra.IndexOf(c) >= 0) sb.Append(c);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        public static string EscapeLike(string text)
        {
            return text == null ? null : text.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        }
    }
}
