using Business.Logging;
using Models;

namespace Web.Custom
{
    /// <summary>
    /// Đọc RequestLogQueue, ghi SQL theo lô (logBatchSize hoặc mỗi FlushInterval). SQL lỗi → spool .jsonl.gz, đẩy lại khi SQL chạy được.
    /// Mỗi CleanupInterval gọi restApi_LogCleanup. Dừng app thì ghi nốt hàng đợi (lỗi thì spool)
    /// </summary>
    public sealed class LogWorker(RequestLogQueue queue, IRequestLogSink sink, RequestLogSpool spool, AppConfig appConfig, ILogger<LogWorker> logger) : BackgroundService
    {
        public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);
        /// <summary>Số file spool đẩy lại mỗi vòng (tránh dồn tải khi SQL vừa lên lại)</summary>
        public int ReplayFilesPerRound { get; init; } = 5;

        private bool _sqlHealthy = true;
        private DateTime _nextCleanup = DateTime.MinValue;

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Không nhận thêm; ExecuteAsync ghi nốt phần còn lại rồi thoát
            queue.Complete();
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            List<RequestLogEntry> batch = new(appConfig.logBatchSize);
            bool open = true;

            while (open)
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    wait.CancelAfter(FlushInterval);
                    try
                    {
                        open = await queue.Reader.WaitToReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Hết FlushInterval hoặc đang dừng: vẫn ghi phần đã có
                    }
                }

                // Ghi hết những gì đang có, mỗi lô tối đa logBatchSize
                while (queue.Reader.TryRead(out RequestLogEntry? entry))
                {
                    batch.Add(entry);
                    if (batch.Count >= appConfig.logBatchSize)
                    {
                        await FlushAsync(batch);
                    }
                }
                await FlushAsync(batch);

                if (stoppingToken.IsCancellationRequested && !queue.Reader.TryPeek(out _))
                {
                    // Dừng: hàng đợi đã cạn (StopAsync đã Complete nên không còn mục mới)
                    break;
                }

                await ReplaySpoolAsync();
                await CleanupIfDueAsync(stoppingToken);
            }
        }

        private async Task FlushAsync(List<RequestLogEntry> batch)
        {
            if (batch.Count == 0)
            {
                return;
            }
            try
            {
                // Không truyền stoppingToken: đang dừng vẫn phải ghi xong lô
                await sink.WriteAsync(batch, CancellationToken.None);
                if (!_sqlHealthy)
                {
                    logger.LogInformation("Ghi log SQL chạy lại được");
                }
                _sqlHealthy = true;
            }
            catch (Exception ex)
            {
                string file = spool.Write(batch);
                if (_sqlHealthy)
                {
                    logger.LogError(ex, "Ghi log SQL lỗi, chuyển sang spool {File}", file);
                }
                _sqlHealthy = false;
            }
            batch.Clear();
        }

        /// <summary>
        /// Đẩy lại file spool cũ trước. Chỉ thử khi lô gần nhất ghi SQL được (không thì đợi lô sau thử giúp)
        /// </summary>
        private async Task ReplaySpoolAsync()
        {
            if (!_sqlHealthy)
            {
                return;
            }
            foreach (string file in spool.Pending().Take(ReplayFilesPerRound))
            {
                try
                {
                    List<RequestLogEntry> entries = RequestLogSpool.Read(file);
                    await sink.WriteAsync(entries, CancellationToken.None);
                    File.Delete(file);
                    logger.LogInformation("Đã đẩy lại {Count} log từ spool {File}", entries.Count, Path.GetFileName(file));
                }
                catch (Exception ex) when (ex is not (InvalidDataException or System.Text.Json.JsonException))
                {
                    _sqlHealthy = false;
                    logger.LogWarning(ex, "Đẩy lại spool {File} lỗi, thử lại sau", Path.GetFileName(file));
                    return;
                }
                catch (Exception ex)
                {
                    // File hỏng: đổi tên để không thử mãi, giữ lại cho người xem
                    File.Move(file, file + ".bad", overwrite: true);
                    logger.LogError(ex, "File spool hỏng {File}, đã đổi thành .bad", Path.GetFileName(file));
                }
            }
        }

        private async Task CleanupIfDueAsync(CancellationToken stoppingToken)
        {
            if (DateTime.UtcNow < _nextCleanup || !_sqlHealthy || stoppingToken.IsCancellationRequested)
            {
                return;
            }
            _nextCleanup = DateTime.UtcNow + CleanupInterval;
            try
            {
                var (requests, bodies, attacks) = await sink.CleanupAsync(appConfig.logRetentionDays, appConfig.attackLogRetentionDays, stoppingToken);
                if (requests + bodies + attacks > 0)
                {
                    logger.LogInformation("Dọn log: {Requests} request, {Bodies} body, {Attacks} sự kiện tấn công", requests, bodies, attacks);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Dọn log lỗi, thử lại sau {Interval}", CleanupInterval);
            }
        }
    }
}
