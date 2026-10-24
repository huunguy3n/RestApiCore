using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using Business.Logging;
using Microsoft.Data.SqlClient;
using Provider;

namespace DataAccess
{
    /// <summary>
    /// Ghi một lô vào restApiLogReq / restApiLogBody / restApiLogAttack bằng SqlBulkCopy trong một transaction
    /// (lô lỗi thì không ghi gì, spool lại cả lô không sinh dòng trùng)
    /// </summary>
    public sealed class SqlRequestLogSink(SqlDatabase database) : IRequestLogSink
    {
        public async Task WriteAsync(IReadOnlyList<RequestLogEntry> entries, CancellationToken cancellationToken = default)
        {
            if (entries.Count == 0)
            {
                return;
            }

            DataTable requests = RequestTable();
            DataTable attacks = AttackTable();
            DataTable bodies = BodyTable();
            HashSet<string> bodyShas = new(StringComparer.Ordinal);

            foreach (RequestLogEntry entry in entries)
            {
                string? requestSha = AddBody(bodies, bodyShas, entry.RequestBody, entry.CreatedAt) ?? entry.RequestSha256;
                string? responseSha = AddBody(bodies, bodyShas, entry.ResponseBody, entry.CreatedAt) ?? entry.ResponseSha256;

                requests.Rows.Add(entry.ReqId, entry.CreatedAt, (object?)entry.UId ?? DBNull.Value, Cut(entry.UserName, 32), Cut(entry.IpAddress, 45),
                    Cut(entry.Method, 10), Cut(entry.Path, 256), Cut(entry.QueryString, 1024), Cut(entry.Form, 32), entry.StatusCode, Cut(entry.ResultCode, 64),
                    entry.DurationMs, entry.RequestBytes, entry.ResponseBytes, (object?)requestSha ?? DBNull.Value, (object?)responseSha ?? DBNull.Value,
                    Cut(entry.ContentSha256, 64), Cut(entry.Nonce, 64), (object?)entry.ClientTimestamp ?? DBNull.Value, Cut(entry.SignatureResult, 32),
                    Cut(entry.IdempotencyKey, 128));

                if (entry.IsAttack)
                {
                    attacks.Rows.Add(entry.ReqId, entry.CreatedAt, Cut(entry.IpAddress, 45), Cut(entry.UserName, 32), Cut(entry.Path, 256), Cut(entry.ResultCode, 64) ?? "UNKNOWN");
                }
            }

            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await database.BeginTransactionAsync(connection, cancellationToken);

            if (bodies.Rows.Count > 0)
            {
                // Body: qua bảng tạm rồi chỉ thêm SHA256 chưa có (loại trùng giữa các lô)
                await database.ExecuteAsync(connection, transaction, "CREATE TABLE #log_body (sha256 CHAR(64) NOT NULL PRIMARY KEY, created_at DATETIME NOT NULL, size INT NOT NULL, content VARBINARY(MAX) NOT NULL)", cancellationToken);
                await database.BulkAsync(connection, transaction, bodies, "#log_body", cancellationToken);
                await database.ExecuteAsync(connection, transaction, """
                    INSERT INTO dbo.restApiLogBody (sha256, created_at, size, content)
                        SELECT a.sha256, a.created_at, a.size, a.content FROM #log_body a
                            WHERE NOT EXISTS (SELECT 1 FROM dbo.restApiLogBody b WHERE b.sha256 = a.sha256)
                    """, cancellationToken);
            }
            await database.BulkAsync(connection, transaction, requests, "dbo.restApiLogReq", cancellationToken);
            if (attacks.Rows.Count > 0)
            {
                await database.BulkAsync(connection, transaction, attacks, "dbo.restApiLogAttack", cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<(int Requests, int Bodies, int Attacks)> CleanupAsync(int retentionDays, int attackRetentionDays, CancellationToken cancellationToken = default)
        {
            var ds = await database.RetrieveAsync("exec restApi_LogCleanup @RetentionDays, @AttackRetentionDays", cancellationToken,
                new SqlParameter("@RetentionDays", SqlDbType.Int) { Value = retentionDays },
                new SqlParameter("@AttackRetentionDays", SqlDbType.Int) { Value = attackRetentionDays });
            DataRow row = ds.Tables[0].Rows[0];
            return (Convert.ToInt32(row["requests"]), Convert.ToInt32(row["bodies"]), Convert.ToInt32(row["attacks"]));
        }

        /// <summary>Băm, nén, thêm body vào bảng (một lần cho mỗi SHA256); trả SHA256</summary>
        private static string? AddBody(DataTable bodies, HashSet<string> shas, byte[]? body, DateTime createdAt)
        {
            if (body == null || body.Length == 0)
            {
                return null;
            }
            string sha = Convert.ToHexStringLower(SHA256.HashData(body));
            if (shas.Add(sha))
            {
                bodies.Rows.Add(sha, createdAt, body.Length, Compress(body));
            }
            return sha;
        }

        public static byte[] Compress(byte[] data)
        {
            using MemoryStream output = new();
            using (GZipStream gzip = new(output, CompressionLevel.Fastest))
            {
                gzip.Write(data);
            }
            return output.ToArray();
        }

        private static object Cut(string? value, int length) => value == null ? DBNull.Value : value.Length > length ? value[..length] : value;

        private static DataTable RequestTable()
        {
            DataTable table = new();
            table.Columns.Add("req_id", typeof(Guid));
            table.Columns.Add("created_at", typeof(DateTime));
            table.Columns.Add("u_id", typeof(int));
            table.Columns.Add("user_name", typeof(string));
            table.Columns.Add("ip_address", typeof(string));
            table.Columns.Add("method", typeof(string));
            table.Columns.Add("path", typeof(string));
            table.Columns.Add("query_string", typeof(string));
            table.Columns.Add("form", typeof(string));
            table.Columns.Add("status_code", typeof(int));
            table.Columns.Add("result_code", typeof(string));
            table.Columns.Add("duration_ms", typeof(int));
            table.Columns.Add("request_bytes", typeof(int));
            table.Columns.Add("response_bytes", typeof(int));
            table.Columns.Add("request_sha256", typeof(string));
            table.Columns.Add("response_sha256", typeof(string));
            table.Columns.Add("content_sha256", typeof(string));
            table.Columns.Add("nonce", typeof(string));
            table.Columns.Add("client_timestamp", typeof(long));
            table.Columns.Add("signature_result", typeof(string));
            table.Columns.Add("idempotency_key", typeof(string));
            return table;
        }

        private static DataTable AttackTable()
        {
            DataTable table = new();
            table.Columns.Add("req_id", typeof(Guid));
            table.Columns.Add("created_at", typeof(DateTime));
            table.Columns.Add("ip_address", typeof(string));
            table.Columns.Add("user_name", typeof(string));
            table.Columns.Add("path", typeof(string));
            table.Columns.Add("code", typeof(string));
            return table;
        }

        private static DataTable BodyTable()
        {
            DataTable table = new();
            table.Columns.Add("sha256", typeof(string));
            table.Columns.Add("created_at", typeof(DateTime));
            table.Columns.Add("size", typeof(int));
            table.Columns.Add("content", typeof(byte[]));
            return table;
        }

    }
}
