using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Models;

namespace Business.Logging
{
    /// <summary>
    /// Một request đã xử lý xong (KE_HOACH_V3 5.2). Không chứa private_key, chữ ký HMAC, JWT, connection string.
    /// Body thô giữ tới khi worker băm và nén; body lớn hơn logMaxBodyBytes chỉ giữ SHA256
    /// </summary>
    public sealed record RequestLogEntry
    {
        public required Guid ReqId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public int? UId { get; init; }
        public string? UserName { get; init; }
        public string? IpAddress { get; init; }
        public required string Method { get; init; }
        public required string Path { get; init; }
        public string? QueryString { get; init; }
        public string? Form { get; init; }
        public required int StatusCode { get; init; }
        public string? ResultCode { get; init; }
        public required int DurationMs { get; init; }
        public int RequestBytes { get; init; }
        public int ResponseBytes { get; init; }
        public byte[]? RequestBody { get; init; }
        public byte[]? ResponseBody { get; init; }
        /// <summary>SHA256 khi body quá lớn để giữ (đã bỏ body)</summary>
        public string? RequestSha256 { get; init; }
        public string? ResponseSha256 { get; init; }
        public string? ContentSha256 { get; init; }
        public string? Nonce { get; init; }
        public long? ClientTimestamp { get; init; }
        public string? SignatureResult { get; init; }
        public string? IdempotencyKey { get; init; }
        public bool IsAttack { get; init; }
    }

    /// <summary>
    /// Mã kết quả coi là sự kiện nghi tấn công, ghi thêm vào restApiLogAttack
    /// </summary>
    public static class AttackCodes
    {
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            "UNAUTHORIZED", "INVALID_SIGNATURE", "MISSING_SIGNATURE", "TIMESTAMP_EXPIRED", "NONCE_REUSED", "CHECKSUM_MISMATCH",
            "ACCOUNT_LOCKED", "ACCESS_DENIED", "TOO_MANY_REQUESTS", "FORBIDDEN", "KEY_NOT_ISSUED"
        };
    }

    /// <summary>
    /// Hàng đợi log trong bộ nhớ, có giới hạn: đầy thì bỏ mục mới và đếm (request không bao giờ chờ log)
    /// </summary>
    public sealed class RequestLogQueue(AppConfig appConfig)
    {
        private readonly Channel<RequestLogEntry> _channel = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(Math.Max(1, appConfig.logQueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
        private long _dropped;

        public ChannelReader<RequestLogEntry> Reader => _channel.Reader;

        /// <summary>Số mục bị bỏ vì hàng đợi đầy (kể từ khi khởi động)</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        public void Enqueue(RequestLogEntry entry)
        {
            if (!_channel.Writer.TryWrite(entry))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        public void Complete() => _channel.Writer.TryComplete();
    }

    public interface IRequestLogSink
    {
        Task WriteAsync(IReadOnlyList<RequestLogEntry> entries, CancellationToken cancellationToken = default);

        /// <summary>restApi_LogCleanup: trả (request, body, attack) đã dọn</summary>
        Task<(int Requests, int Bodies, int Attacks)> CleanupAsync(int retentionDays, int attackRetentionDays, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Ghi một lô vào restApiLogReq / restApiLogBody / restApiLogAttack bằng SqlBulkCopy trong một transaction
    /// (lô lỗi thì không ghi gì, spool lại cả lô không sinh dòng trùng)
    /// </summary>
    public sealed class SqlRequestLogSink(AppConfig appConfig) : IRequestLogSink
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

            await using var connection = new SqlConnection(appConfig.appConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            if (bodies.Rows.Count > 0)
            {
                // Body: qua bảng tạm rồi chỉ thêm SHA256 chưa có (loại trùng giữa các lô)
                await ExecuteAsync(connection, transaction, "CREATE TABLE #log_body (sha256 CHAR(64) NOT NULL PRIMARY KEY, created_at DATETIME NOT NULL, size INT NOT NULL, content VARBINARY(MAX) NOT NULL)", cancellationToken);
                await BulkAsync(connection, transaction, bodies, "#log_body", cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO dbo.restApiLogBody (sha256, created_at, size, content)
                        SELECT a.sha256, a.created_at, a.size, a.content FROM #log_body a
                            WHERE NOT EXISTS (SELECT 1 FROM dbo.restApiLogBody b WHERE b.sha256 = a.sha256)
                    """, cancellationToken);
            }
            await BulkAsync(connection, transaction, requests, "dbo.restApiLogReq", cancellationToken);
            if (attacks.Rows.Count > 0)
            {
                await BulkAsync(connection, transaction, attacks, "dbo.restApiLogAttack", cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<(int Requests, int Bodies, int Attacks)> CleanupAsync(int retentionDays, int attackRetentionDays, CancellationToken cancellationToken = default)
        {
            var ds = await Provider.Provider.RetrieveAsync(appConfig.appConnectionString, "exec restApi_LogCleanup @RetentionDays, @AttackRetentionDays", cancellationToken,
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

        private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = Provider.Provider.CommandTimeout };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task BulkAsync(SqlConnection connection, SqlTransaction transaction, DataTable table, string destination, CancellationToken cancellationToken)
        {
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = destination, BulkCopyTimeout = Provider.Provider.CommandTimeout };
            foreach (DataColumn column in table.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }
            await bulk.WriteToServerAsync(table, cancellationToken);
        }
    }

    /// <summary>
    /// SQL lỗi thì ghi lô ra file .jsonl.gz trong logDirectory/spool, đẩy lại khi SQL chạy được (file cũ trước)
    /// </summary>
    public sealed class RequestLogSpool(string directory)
    {
        public string Directory { get; } = directory;

        public string Write(IReadOnlyList<RequestLogEntry> entries)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string file = Path.Combine(Directory, $"log-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.jsonl.gz");
            string temp = file + ".tmp";
            using (FileStream stream = File.Create(temp))
            using (GZipStream gzip = new(stream, CompressionLevel.Fastest))
            using (StreamWriter writer = new(gzip, new UTF8Encoding(false)))
            {
                foreach (RequestLogEntry entry in entries)
                {
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                }
            }
            // Đổi tên sau khi ghi xong: file đang ghi dở không bị đẩy lại
            File.Move(temp, file);
            return file;
        }

        public IReadOnlyList<string> Pending() => System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "log-*.jsonl.gz").Order(StringComparer.Ordinal).ToList()
            : [];

        public static List<RequestLogEntry> Read(string file)
        {
            using FileStream stream = File.OpenRead(file);
            using GZipStream gzip = new(stream, CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);
            List<RequestLogEntry> entries = [];
            while (reader.ReadLine() is string line)
            {
                if (line.Length > 0)
                {
                    entries.Add(JsonSerializer.Deserialize<RequestLogEntry>(line)!);
                }
            }
            return entries;
        }
    }
}
