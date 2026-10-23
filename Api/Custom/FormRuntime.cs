using System.Text.RegularExpressions;
using System.Threading.Channels;
using Business;
using DataAccess;
using Models;

namespace Web.Custom
{
    /// <summary>
    /// Bảng route form v3 đang dùng và mẫu ký tự đặc biệt. Catalog thay nguyên khối khi nạp lại (catalog bất biến),
    /// request đang chạy giữ catalog cũ tới khi xong
    /// </summary>
    public sealed class FormRuntime(ImportFormCatalog catalog, Regex? irregular)
    {
        private ImportFormCatalog _catalog = catalog;

        public ImportFormCatalog Catalog => Volatile.Read(ref _catalog);
        public Regex? Irregular { get; } = irregular;

        public void Replace(ImportFormCatalog catalog) => Volatile.Write(ref _catalog, catalog);
    }

    /// <summary>
    /// Nạp App_Data/Form và gắn kiểu theo cột thật. Mỗi lần nạp đọc lại cấu trúc bảng (bảng có thể đã thêm cột)
    /// </summary>
    public sealed class FormLoader(AppConfig appConfig, ILogger<ImportFormCatalog> logger)
    {
        public string Directory => Path.Combine(appConfig.pathDirectory, "App_Data", "Form");

        public ImportFormCatalog Load()
        {
            var columns = new ColumnSchemaRepository(appConfig);
            var schemaCache = new Dictionary<string, IReadOnlyDictionary<string, ColumnInfo>?>(StringComparer.OrdinalIgnoreCase);

            return ImportFormCatalog.LoadDirectory(Directory, logger,
                form => ColumnTypeResolver.Resolve(form, table =>
                {
                    if (!schemaCache.TryGetValue(table, out var schema))
                    {
                        schema = schemaCache[table] = columns.GetColumnsAsync(table).GetAwaiter().GetResult();
                    }
                    return schema;
                }));
        }
    }

    /// <summary>
    /// Theo dõi App_Data/Form/*.xml, file đổi thì nạp lại toàn bộ catalog sau một khoảng gom sự kiện.
    /// Nạp lỗi ngoài lỗi XML (vd mất kết nối SQL) thì giữ catalog cũ
    /// </summary>
    public sealed class FormReloader(FormRuntime runtime, Func<ImportFormCatalog> load, string directory, ILogger<FormReloader> logger) : BackgroundService
    {
        /// <summary>Gom các sự kiện liên tiếp (trình soạn thảo ghi file nhiều lần) thành một lần nạp</summary>
        public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

        private readonly Channel<string> _changes = Channel.CreateUnbounded<string>();
        private FileSystemWatcher? _watcher;

        /// <summary>
        /// Bật watcher ngay trong StartAsync: từ .NET 10 ExecuteAsync chạy trên luồng nền, bật ở đó sẽ lỡ thay đổi
        /// xảy ra giữa lúc nạp lần đầu và lúc watcher chạy
        /// </summary>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            if (System.IO.Directory.Exists(directory))
            {
                _watcher = new FileSystemWatcher(directory, "*.xml")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                FileSystemEventHandler onChange = (_, e) => _changes.Writer.TryWrite(e.Name ?? "");
                _watcher.Changed += onChange;
                _watcher.Created += onChange;
                _watcher.Deleted += onChange;
                _watcher.Renamed += (_, e) => _changes.Writer.TryWrite(e.Name ?? "");
                _watcher.Error += (_, e) => logger.LogError(e.GetException(), "FileSystemWatcher lỗi, form có thể không tự nạp lại");
                _watcher.EnableRaisingEvents = true;
            }
            else
            {
                logger.LogWarning("Không có thư mục form {Directory}, không theo dõi thay đổi", directory);
            }
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_watcher == null)
            {
                return;
            }

            try
            {
                while (await _changes.Reader.WaitToReadAsync(stoppingToken))
                {
                    await Task.Delay(Debounce, stoppingToken);

                    HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
                    while (_changes.Reader.TryRead(out string? file))
                    {
                        files.Add(file);
                    }

                    Reload(files);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        public override void Dispose()
        {
            _watcher?.Dispose();
            base.Dispose();
        }

        private void Reload(IEnumerable<string> files)
        {
            try
            {
                ImportFormCatalog catalog = load();
                runtime.Replace(catalog);
                logger.LogInformation("Nạp lại form v3 do đổi {Files}: {Count} form, {Errors} lỗi", string.Join(", ", files), catalog.Forms.Count, catalog.Errors.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Nạp lại form v3 thất bại, giữ bảng route cũ");
            }
        }
    }
}
