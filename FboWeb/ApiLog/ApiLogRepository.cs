using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FboWeb.ApiLog
{
    /// <summary>
    /// Đọc restApiLogReq / restApiLogBody / restApiLogAttack trên DB app, ghi zcApiLogViewAudit khi mở body.
    /// SQL Server 2008: ROW_NUMBER, mọi giá trị lọc đi bằng tham số. Kết quả dựng sẵn dạng JSON camelCase cho api-log.js.
    /// </summary>
    public sealed class ApiLogRepository
    {
        public const int MaxBodyBytes = 256 * 1024;

        // Có Request ID thì bỏ qua khoảng ngày (giống restApi_LogSearch): đối tác thường gửi mã request của ngày cũ
        private const string BaseWhere = @"(@req IS NOT NULL AND req_id = @req OR @req IS NULL AND created_at >= @from_time AND created_at < @to_time)
			AND (@user IS NULL OR user_name = @user) AND (@path IS NULL OR path LIKE @path + N'%')";

        // Thống kê chip lọc nhanh chỉ áp BaseWhere: số trên chip không đổi khi bấm qua lại giữa các chip
        private const string Where = BaseWhere + @"
			AND (@status_from IS NULL OR status_code BETWEEN @status_from AND @status_to)
			AND (@result IS NULL OR result_code = @result) AND (@result_not IS NULL OR result_code <> @result_not)";

        private const string SummaryColumns = @"req_id, created_at, user_name, ip_address, method, path, query_string, form, status_code, result_code, duration_ms,
			request_bytes, response_bytes, signature_result, request_sha256, response_sha256";

        private readonly string connectionString;

        public ApiLogRepository(string connectionString)
        {
            this.connectionString = connectionString;
        }

        public Dictionary<string, object> List(ApiLogFilter filter)
        {
            int first = (filter.Page - 1) * filter.PageSize + 1;
            DataSet ds = new DataSet();
            using (SqlConnection sql = new SqlConnection(connectionString))
            using (SqlCommand command = new SqlCommand(@"
				SELECT COUNT(1) AS all_count,
					SUM(CASE WHEN status_code BETWEEN 200 AND 299 THEN 1 ELSE 0 END) AS http2xx,
					SUM(CASE WHEN status_code BETWEEN 300 AND 399 THEN 1 ELSE 0 END) AS http3xx,
					SUM(CASE WHEN status_code BETWEEN 400 AND 499 THEN 1 ELSE 0 END) AS http4xx,
					SUM(CASE WHEN status_code >= 500 THEN 1 ELSE 0 END) AS http5xx,
					SUM(CASE WHEN result_code IS NOT NULL AND result_code <> 'SUCCESS' THEN 1 ELSE 0 END) AS result_not_success,
					SUM(CASE WHEN result_code IS NULL THEN 1 ELSE 0 END) AS result_missing,
					AVG(CAST(duration_ms AS BIGINT)) AS avg_ms, MAX(duration_ms) AS max_ms
				FROM restApiLogReq WHERE " + BaseWhere + @" OPTION (RECOMPILE)
				SELECT COUNT(1) AS total FROM restApiLogReq WHERE " + Where + @" OPTION (RECOMPILE)
				SELECT t.* FROM (SELECT " + SummaryColumns + @", ROW_NUMBER() OVER (ORDER BY created_at DESC, id DESC) AS rn
					FROM restApiLogReq WHERE " + Where + @") t WHERE t.rn BETWEEN @rn_from AND @rn_to ORDER BY t.rn OPTION (RECOMPILE)", sql))
            {
                command.Parameters.Add("@from_time", SqlDbType.DateTime).Value = filter.From;
                command.Parameters.Add("@to_time", SqlDbType.DateTime).Value = filter.ToExclusive;
                command.Parameters.Add("@user", SqlDbType.VarChar, 32).Value = Param(filter.UserName);
                command.Parameters.Add("@path", SqlDbType.NVarChar, 300).Value = Param(filter.PathPrefix);
                command.Parameters.Add("@status_from", SqlDbType.Int).Value = filter.StatusFrom.HasValue ? (object)filter.StatusFrom.Value : DBNull.Value;
                command.Parameters.Add("@status_to", SqlDbType.Int).Value = filter.StatusTo.HasValue ? (object)filter.StatusTo.Value : DBNull.Value;
                command.Parameters.Add("@result", SqlDbType.VarChar, 64).Value = Param(filter.ResultCode);
                command.Parameters.Add("@result_not", SqlDbType.VarChar, 64).Value = Param(filter.ResultNot);
                command.Parameters.Add("@req", SqlDbType.UniqueIdentifier).Value = filter.ReqId.HasValue ? (object)filter.ReqId.Value : DBNull.Value;
                command.Parameters.Add("@rn_from", SqlDbType.Int).Value = first;
                command.Parameters.Add("@rn_to", SqlDbType.Int).Value = first + filter.PageSize - 1;
                new SqlDataAdapter(command).Fill(ds);
            }

            DataRow totals = ds.Tables[0].Rows[0];
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            foreach (DataRow row in ds.Tables[2].Rows) items.Add(Summary(row));
            return new Dictionary<string, object>
            {
                { "success", true },
                { "total", Int(ds.Tables[1].Rows[0]["total"]) },
                { "page", filter.Page },
                { "pageSize", filter.PageSize },
                { "ignoredDateRange", filter.ReqId.HasValue },
                { "stats", new Dictionary<string, object>
                    {
                        { "all", Int(totals["all_count"]) },
                        { "http2xx", Int(totals["http2xx"]) },
                        { "http3xx", Int(totals["http3xx"]) },
                        { "http4xx", Int(totals["http4xx"]) },
                        { "http5xx", Int(totals["http5xx"]) },
                        { "resultNotSuccess", Int(totals["result_not_success"]) },
                        { "resultMissing", Int(totals["result_missing"]) },
                        { "avgMs", totals["avg_ms"] == DBNull.Value ? null : (object)Convert.ToInt64(totals["avg_ms"], CultureInfo.InvariantCulture) },
                        { "maxMs", totals["max_ms"] == DBNull.Value ? null : (object)Int(totals["max_ms"]) }
                    }
                },
                { "items", items }
            };
        }

        /// <summary>Metadata một request (không body, không ghi audit). null = không có</summary>
        public Dictionary<string, object> Detail(Guid reqId)
        {
            DataSet ds = new DataSet();
            using (SqlConnection sql = new SqlConnection(connectionString))
            using (SqlCommand command = new SqlCommand(@"
				SELECT TOP 1 " + SummaryColumns + @", content_sha256, nonce, client_timestamp, idempotency_key,
					(SELECT b.size FROM restApiLogBody b WHERE b.sha256 = r.request_sha256) AS request_stored_size,
					(SELECT b.size FROM restApiLogBody b WHERE b.sha256 = r.response_sha256) AS response_stored_size
				FROM restApiLogReq r WHERE req_id = @req ORDER BY id DESC
				SELECT TOP 50 created_at, ip_address, code FROM restApiLogAttack WHERE req_id = @req ORDER BY created_at, id", sql))
            {
                command.Parameters.Add("@req", SqlDbType.UniqueIdentifier).Value = reqId;
                new SqlDataAdapter(command).Fill(ds);
            }
            if (ds.Tables[0].Rows.Count == 0) return null;

            DataRow row = ds.Tables[0].Rows[0];
            List<Dictionary<string, object>> attacks = new List<Dictionary<string, object>>();
            foreach (DataRow a in ds.Tables[1].Rows)
            {
                attacks.Add(new Dictionary<string, object> { { "createdAt", Time(a["created_at"]) }, { "ipAddress", Text(a["ip_address"]) }, { "code", Text(a["code"]) } });
            }
            return new Dictionary<string, object>
            {
                { "success", true },
                { "summary", Summary(row) },
                { "headers", new Dictionary<string, object>
                    {
                        { "contentSha256", Text(row["content_sha256"]) },
                        { "nonce", Text(row["nonce"]) },
                        { "clientTimestamp", row["client_timestamp"] == DBNull.Value ? null : (object)Convert.ToInt64(row["client_timestamp"], CultureInfo.InvariantCulture) },
                        { "idempotencyKey", Text(row["idempotency_key"]) }
                    }
                },
                { "requestBody", BodyState(row["request_sha256"], row["request_stored_size"]) },
                { "responseBody", BodyState(row["response_sha256"], row["response_stored_size"]) },
                { "attacks", attacks }
            };
        }

        /// <summary>Body đã giải nén (tối đa MaxBodyBytes + 1 byte để biết bị cắt). null = không có request</summary>
        public LoggedBodies ReadBodies(Guid reqId)
        {
            using (SqlConnection sql = new SqlConnection(connectionString))
            using (SqlCommand command = new SqlCommand(@"
				SELECT TOP 1 r.request_sha256, r.response_sha256, rq.content AS request_content, rs.content AS response_content
				FROM restApiLogReq r
				LEFT JOIN restApiLogBody rq ON rq.sha256 = r.request_sha256
				LEFT JOIN restApiLogBody rs ON rs.sha256 = r.response_sha256
				WHERE r.req_id = @req ORDER BY r.id DESC", sql))
            {
                command.Parameters.Add("@req", SqlDbType.UniqueIdentifier).Value = reqId;
                DataTable table = new DataTable();
                new SqlDataAdapter(command).Fill(table);
                if (table.Rows.Count == 0) return null;
                DataRow row = table.Rows[0];
                return new LoggedBodies
                {
                    RequestKept = row["request_sha256"] != DBNull.Value,
                    ResponseKept = row["response_sha256"] != DBNull.Value,
                    Request = Unzip(row["request_content"]),
                    Response = Unzip(row["response_content"])
                };
            }
        }

        /// <summary>Ghi vết mở body. Lỗi SQL ném ra để handler không trả body</summary>
        public void WriteAudit(int userId, string userName, Guid reqId, string ipAddress)
        {
            using (SqlConnection sql = new SqlConnection(connectionString))
            using (SqlCommand audit = new SqlCommand("INSERT INTO zcApiLogViewAudit (user_id, user_name, req_id, ip_address) VALUES (@u, @n, @r, @ip)", sql))
            {
                audit.Parameters.Add("@u", SqlDbType.Int).Value = userId;
                audit.Parameters.Add("@n", SqlDbType.NVarChar, 128).Value = userName.Length > 128 ? userName.Substring(0, 128) : userName;
                audit.Parameters.Add("@r", SqlDbType.UniqueIdentifier).Value = reqId;
                audit.Parameters.Add("@ip", SqlDbType.VarChar, 45).Value = Param(ApiLogQuery.Clean(ipAddress, 45, ".:"));
                sql.Open();
                audit.ExecuteNonQuery();
            }
        }

        private static Dictionary<string, object> Summary(DataRow row)
        {
            return new Dictionary<string, object>
            {
                { "reqId", ((Guid)row["req_id"]).ToString() },
                { "createdAt", Time(row["created_at"]) },
                { "userName", Text(row["user_name"]) },
                { "ipAddress", Text(row["ip_address"]) },
                { "method", Text(row["method"]) },
                { "path", Text(row["path"]) },
                { "queryString", LogRedactor.RedactQuery(Text(row["query_string"])) },
                { "form", Text(row["form"]) },
                { "statusCode", Int(row["status_code"]) },
                { "resultCode", Text(row["result_code"]) },
                { "durationMs", Int(row["duration_ms"]) },
                { "requestBytes", Int(row["request_bytes"]) },
                { "responseBytes", Int(row["response_bytes"]) },
                { "signatureResult", Text(row["signature_result"]) },
                { "hasBody", row["request_sha256"] != DBNull.Value || row["response_sha256"] != DBNull.Value }
            };
        }

        /// <summary>none: không ghi body (rỗng hoặc route không lưu); missing: chỉ có SHA256 (vượt ngưỡng lưu hoặc đã dọn); stored</summary>
        private static Dictionary<string, object> BodyState(object sha, object storedSize)
        {
            string state = sha == DBNull.Value ? "none" : storedSize == DBNull.Value ? "missing" : "stored";
            return new Dictionary<string, object>
            {
                { "state", state },
                { "size", storedSize == DBNull.Value ? null : (object)Int(storedSize) },
                { "tooLarge", storedSize != DBNull.Value && Int(storedSize) > MaxBodyBytes }
            };
        }

        private static byte[] Unzip(object content)
        {
            byte[] data = content as byte[];
            if (data == null) return null;
            using (GZipStream gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while (output.Length <= MaxBodyBytes && (read = gzip.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
                return output.ToArray();
            }
        }

        private static string Time(object value)
        {
            return value == DBNull.Value ? null : ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private static string Text(object value)
        {
            return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int Int(object value)
        {
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static object Param(string value)
        {
            return value == null ? (object)DBNull.Value : value;
        }
    }

    public sealed class LoggedBodies
    {
        /// <summary>Request có body được ghi nhận (có SHA256), dù nội dung còn hay không</summary>
        public bool RequestKept { get; set; }
        public bool ResponseKept { get; set; }
        public byte[] Request { get; set; }
        public byte[] Response { get; set; }

        /// <summary>Body → (state, text) đã che bí mật. state: none | missing | tooLarge | notJson | json</summary>
        public static Dictionary<string, object> Present(bool kept, byte[] body)
        {
            string state;
            string text = null;
            if (!kept) state = "none";
            else if (body == null) state = "missing";
            else if (body.Length > ApiLogRepository.MaxBodyBytes) state = "tooLarge";
            else
            {
                string redacted;
                if (LogRedactor.TryRedactJson(new UTF8Encoding(false).GetString(body), out redacted)) { state = "json"; text = redacted; }
                else state = "notJson";
            }
            return new Dictionary<string, object> { { "state", state }, { "text", text }, { "size", body == null ? null : (object)body.Length } };
        }
    }
}
