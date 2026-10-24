using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Models;

namespace Business.Logging
{
    /// <summary>
    /// Một request đã xử lý xong (KE_HOACH 5.2). Không chứa private_key, chữ ký HMAC, JWT, connection string.
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
            "UNAUTHORIZED", "INVALID_SIGNATURE", "MISSING_SIGNATURE", "TIMESTAMP_EXPIRED", "NONCE_REUSED",
            "ACCOUNT_LOCKED", "ACCESS_DENIED", "TOO_MANY_REQUESTS", "FORBIDDEN", "KEY_NOT_ISSUED",
            // Portal: dò mật khẩu, dùng token portal gọi route nghiệp vụ
            "INVALID_CREDENTIAL", "PORTAL_TOKEN_NOT_ALLOWED"
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

    /// <summary>Nơi ghi log bền vững. Cài đặt SQL: DataAccess.SqlRequestLogSink</summary>
    public interface IRequestLogSink
    {
        Task WriteAsync(IReadOnlyList<RequestLogEntry> entries, CancellationToken cancellationToken = default);

        /// <summary>restApi_LogCleanup: trả (request, body, attack) đã dọn</summary>
        Task<(int Requests, int Bodies, int Attacks)> CleanupAsync(int retentionDays, int attackRetentionDays, CancellationToken cancellationToken = default);
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
