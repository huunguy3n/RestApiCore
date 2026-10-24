using System.Data;
using System.IO.Compression;
using Business;
using Microsoft.Data.SqlClient;
using Provider;

namespace DataAccess
{
    /// <summary>Đọc log request cho portal. SQL Server 2008: ROW_NUMBER; mọi giá trị lọc đi bằng tham số</summary>
    public class LogViewRepository(SqlDatabase database) : IRequestLogReader
    {
        public async Task<LogPage> ListAsync(LogFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            int from = (page - 1) * pageSize + 1;
            const string where = """
                created_at >= @from_time AND created_at < @to_time
                AND (@user IS NULL OR user_name = @user) AND (@form IS NULL OR form = @form)
                AND (@status IS NULL OR status_code = @status) AND (@req IS NULL OR req_id = @req)
                """;
            DataSet ds = await database.RetrieveAsync($"""
                SELECT COUNT(1) AS total FROM restApiLogReq WHERE {where}
                SELECT t.* FROM
                (
                    SELECT req_id, created_at, user_name, ip_address, method, path, form, status_code, result_code, duration_ms, request_bytes, response_bytes, signature_result,
                        ROW_NUMBER() OVER (ORDER BY created_at DESC, id DESC) AS rn
                    FROM restApiLogReq WHERE {where}
                ) t WHERE t.rn BETWEEN @rn_from AND @rn_to ORDER BY t.rn
                """, cancellationToken,
                new SqlParameter("@from_time", SqlDbType.DateTime) { Value = filter.From },
                new SqlParameter("@to_time", SqlDbType.DateTime) { Value = filter.To },
                new SqlParameter("@user", SqlDbType.VarChar, 32) { Value = (object?)filter.UserName ?? DBNull.Value },
                new SqlParameter("@form", SqlDbType.VarChar, 32) { Value = (object?)filter.Form ?? DBNull.Value },
                new SqlParameter("@status", SqlDbType.Int) { Value = (object?)filter.StatusCode ?? DBNull.Value },
                new SqlParameter("@req", SqlDbType.UniqueIdentifier) { Value = (object?)filter.ReqId ?? DBNull.Value },
                new SqlParameter("@rn_from", SqlDbType.Int) { Value = from },
                new SqlParameter("@rn_to", SqlDbType.Int) { Value = from + pageSize - 1 });

            return new LogPage(Convert.ToInt32(ds.Tables[0].Rows[0]["total"]), ds.Tables[1].Rows.Cast<DataRow>().Select(Map).ToList());
        }

        public async Task<LogRequestDetail?> GetAsync(Guid reqId, int maxBodyBytes, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT TOP 1 r.req_id, r.created_at, r.user_name, r.ip_address, r.method, r.path, r.form, r.status_code, r.result_code, r.duration_ms,
                    r.request_bytes, r.response_bytes, r.signature_result, r.query_string, rq.content AS request_content, rs.content AS response_content
                FROM restApiLogReq r
                LEFT JOIN restApiLogBody rq ON rq.sha256 = r.request_sha256
                LEFT JOIN restApiLogBody rs ON rs.sha256 = r.response_sha256
                WHERE r.req_id = @req
                """, cancellationToken, new SqlParameter("@req", SqlDbType.UniqueIdentifier) { Value = reqId });

            if (ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }
            DataRow row = ds.Tables[0].Rows[0];
            return new LogRequestDetail(Map(row), row["query_string"] as string, Unzip(row["request_content"], maxBodyBytes), Unzip(row["response_content"], maxBodyBytes));
        }

        /// <summary>
        /// Hiệu suất theo form: log chi tiết còn giữ (restApiLogReq, index IX_restApiLogReq_perf) cộng phần đã dọn vào restApiLogAgg.
        /// Hai nguồn không trùng nhau vì restApi_LogCleanup xoá dòng chi tiết sau khi cộng vào bảng tổng hợp
        /// </summary>
        public async Task<IReadOnlyList<FormPerformance>> PerformanceAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT a.form, SUM(a.requests) AS requests, SUM(a.errors) AS errors, SUM(a.total_ms) AS total_ms, MAX(a.max_ms) AS max_ms
                	 , SUM(a.lt100) AS lt100, SUM(a.lt500) AS lt500, SUM(a.lt2000) AS lt2000, SUM(a.ge2000) AS ge2000
                	FROM
                	(
                		SELECT ISNULL(b.form, '') AS form, COUNT(1) AS requests, SUM(CASE WHEN b.status_code >= 400 THEN 1 ELSE 0 END) AS errors
                			 , SUM(CAST(b.duration_ms AS BIGINT)) AS total_ms, MAX(b.duration_ms) AS max_ms
                			 , SUM(CASE WHEN b.duration_ms < 100 THEN 1 ELSE 0 END) AS lt100
                			 , SUM(CASE WHEN b.duration_ms >= 100 AND b.duration_ms < 500 THEN 1 ELSE 0 END) AS lt500
                			 , SUM(CASE WHEN b.duration_ms >= 500 AND b.duration_ms < 2000 THEN 1 ELSE 0 END) AS lt2000
                			 , SUM(CASE WHEN b.duration_ms >= 2000 THEN 1 ELSE 0 END) AS ge2000
                			FROM restApiLogReq b
                			WHERE b.created_at >= @from_time AND b.created_at < @to_time
                			GROUP BY ISNULL(b.form, '')
                		UNION ALL
                		SELECT c.form, SUM(c.request_count), SUM(CASE WHEN c.status_code >= 400 THEN c.request_count ELSE 0 END)
                			 , SUM(c.total_ms), MAX(c.max_ms), SUM(c.count_lt100), SUM(c.count_lt500), SUM(c.count_lt2000), SUM(c.count_ge2000)
                			FROM restApiLogAgg c
                			WHERE c.log_date >= DATEADD(DAY, DATEDIFF(DAY, 0, @from_time), 0) AND c.log_date < @to_time
                			GROUP BY c.form
                	) a
                	GROUP BY a.form
                	ORDER BY SUM(a.requests) DESC
                """, cancellationToken,
                new SqlParameter("@from_time", SqlDbType.DateTime) { Value = from },
                new SqlParameter("@to_time", SqlDbType.DateTime) { Value = to });

            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new FormPerformance(r["form"].ToString()!.Trim(), Convert.ToInt32(r["requests"]),
                Convert.ToInt32(r["errors"]), Convert.ToInt64(r["total_ms"]), Convert.ToInt32(r["max_ms"]), Convert.ToInt32(r["lt100"]),
                Convert.ToInt32(r["lt500"]), Convert.ToInt32(r["lt2000"]), Convert.ToInt32(r["ge2000"]))).ToList();
        }

        private static LogRequestSummary Map(DataRow row) => new((Guid)row["req_id"], (DateTime)row["created_at"], row["user_name"] as string, row["ip_address"] as string,
            (string)row["method"], (string)row["path"], row["form"] as string, Convert.ToInt32(row["status_code"]), row["result_code"] as string,
            Convert.ToInt32(row["duration_ms"]), Convert.ToInt32(row["request_bytes"]), Convert.ToInt32(row["response_bytes"]), row["signature_result"] as string);

        /// <summary>Giải nén GZip, cắt ở maxBytes (không giải nén quá cỡ vào bộ nhớ)</summary>
        private static byte[]? Unzip(object content, int maxBytes)
        {
            if (content is not byte[] data)
            {
                return null;
            }
            using GZipStream gzip = new(new MemoryStream(data), CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[8192];
            int read;
            while (output.Length < maxBytes && (read = gzip.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - output.Length))) > 0)
            {
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}
