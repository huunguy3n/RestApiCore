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
