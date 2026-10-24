using System.Collections.Concurrent;
using Business;
using DataAccess;
using Microsoft.Extensions.Configuration;
using Models;
using Models.Options;
using Provider;
using Security;

namespace WebPortal.Portal
{
    /// <summary>
    /// Kết nối tới MỘT dự án, đọc từ chính folder restApi của dự án (appsettings.json + appsettings.Local.json).
    /// Giá trị nhạy cảm (connection string, KeyEncryptionKey) chỉ nằm trong đối tượng này ở bộ nhớ: không log, không render, không ghi PortalDb
    /// </summary>
    public sealed class ProjectConnection
    {
        internal ProjectConnection(string apiFolder, string connectionString, string keyEncryptionKey, string projectCode, string environment)
        {
            ApiFolder = apiFolder;
            Database = new SqlDatabase(connectionString, 60);
            Admin = new ProjectAdminRepository(Database);
            Logs = new LogViewRepository(Database);
            _keyEncryptionKey = keyEncryptionKey;
            ProjectCode = projectCode;
            Environment = environment;
        }

        private readonly string _keyEncryptionKey;

        public string ApiFolder { get; }
        public SqlDatabase Database { get; }
        public ProjectAdminRepository Admin { get; }
        public LogViewRepository Logs { get; }
        /// <summary>appSettings:projectCode trong folder (đối chiếu với portalProject.ma_da)</summary>
        public string ProjectCode { get; }
        public string Environment { get; }

        public string FormDirectory => Path.Combine(ApiFolder, "App_Data", "Form");

        /// <summary>Null nếu folder chưa có Security:KeyEncryptionKey hợp lệ (không cấp/xem private_key được)</summary>
        public PrivateKeyProtector? Protector()
        {
            try
            {
                return new PrivateKeyProtector(_keyEncryptionKey);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Form khai trong App_Data/Form (không gắn kiểu cột thật: chỉ để hiển thị). Form lỗi nằm trong Errors</summary>
        public ImportFormCatalog LoadForms() => ImportFormCatalog.LoadDirectory(FormDirectory);

        /// <summary>Bảng mã lỗi App_Data/Options/Message.xml của dự án</summary>
        public MessageList LoadMessages()
        {
            try
            {
                return new OptionsRepository(new AppConfig { pathDirectory = ApiFolder }).Messages ?? new MessageList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return new MessageList();
            }
        }
    }

    public sealed record FolderCheck(bool Ok, string Message);

    public interface IProjectConnectionResolver
    {
        /// <summary>Ném ProjectFolderException nếu folder không hợp lệ / thiếu cấu hình</summary>
        ProjectConnection Resolve(PortalProject project);

        /// <summary>Kiểm folder trước khi lưu: nằm dưới gốc cho phép, có appsettings + App_Data\Form, kết nối được SQL, đã chạy sql/009</summary>
        Task<FolderCheck> CheckAsync(string apiFolder, string maDa, CancellationToken cancellationToken = default);

        bool IsAllowedFolder(string apiFolder);
    }

    public sealed class ProjectFolderException(string message) : Exception(message);

    public sealed class ProjectConnectionResolver(PortalSettings settings) : IProjectConnectionResolver
    {
        private sealed record Cached(DateTime Stamp, ProjectConnection Connection);

        private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

        public bool IsAllowedFolder(string apiFolder)
        {
            if (string.IsNullOrWhiteSpace(apiFolder) || apiFolder.Contains("..", StringComparison.Ordinal) || !Path.IsPathFullyQualified(apiFolder))
            {
                return false;
            }
            string full;
            try
            {
                full = Path.GetFullPath(apiFolder).TrimEnd('\\', '/') + "\\";
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
            return settings.AllowedApiRoots.Any(root => root.Length > 0
                && full.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
        }

        public ProjectConnection Resolve(PortalProject project)
        {
            if (!IsAllowedFolder(project.ApiFolder))
            {
                throw new ProjectFolderException("Folder Api của dự án không nằm dưới Portal:AllowedApiRoots");
            }
            string local = Path.Combine(project.ApiFolder, "appsettings.Local.json"), main = Path.Combine(project.ApiFolder, "appsettings.json");
            if (!File.Exists(local))
            {
                throw new ProjectFolderException("Không đọc được appsettings.Local.json trong folder Api của dự án");
            }
            DateTime stamp = File.GetLastWriteTimeUtc(local) > (File.Exists(main) ? File.GetLastWriteTimeUtc(main) : DateTime.MinValue)
                ? File.GetLastWriteTimeUtc(local) : File.GetLastWriteTimeUtc(main);
            string key = project.ApiFolder;
            if (_cache.TryGetValue(key, out Cached? cached) && cached.Stamp == stamp)
            {
                return cached.Connection;
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(main, optional: true)
                .AddJsonFile(local, optional: false)
                .Build();
            string connectionString = config.GetConnectionString("appConnectionString") ?? "";
            if (connectionString.Length == 0)
            {
                throw new ProjectFolderException("appsettings.Local.json của dự án thiếu connectionStrings:appConnectionString");
            }
            ProjectConnection connection = new(project.ApiFolder, connectionString, config["Security:KeyEncryptionKey"] ?? "",
                config["appSettings:projectCode"] ?? "", config["appSettings:environment"] ?? "DEV");
            _cache[key] = new Cached(stamp, connection);
            return connection;
        }

        public async Task<FolderCheck> CheckAsync(string apiFolder, string maDa, CancellationToken cancellationToken = default)
        {
            if (!IsAllowedFolder(apiFolder))
            {
                return new FolderCheck(false, "Đường dẫn phải là đường dẫn đầy đủ nằm dưới gốc cho phép (Portal:AllowedApiRoots), không có '..'.");
            }
            if (!Directory.Exists(Path.Combine(apiFolder, "App_Data", "Form")))
            {
                return new FolderCheck(false, "Folder không có App_Data\\Form (không phải folder restApi?).");
            }
            ProjectConnection connection;
            try
            {
                connection = Resolve(new PortalProject(0, maDa, apiFolder, "", true, null, null));
            }
            catch (ProjectFolderException ex)
            {
                return new FolderCheck(false, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
            {
                return new FolderCheck(false, "Không đọc được cấu hình trong folder (quyền truy cập hoặc JSON hỏng).");
            }

            string? schema;
            try
            {
                schema = await connection.Admin.CheckSchemaAsync(cancellationToken);
            }
            catch (DatabaseException)
            {
                return new FolderCheck(false, "Không chạy được truy vấn trên DB dự án.");
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                // Không đưa nội dung lỗi SQL ra giao diện: có thể chứa tên máy chủ/DB
                return new FolderCheck(false, "Không kết nối được SQL bằng connection string trong folder.");
            }
            if (schema != null)
            {
                return new FolderCheck(false, schema);
            }

            List<string> notes = [];
            if (connection.Protector() == null)
            {
                notes.Add("thiếu Security:KeyEncryptionKey (chưa cấp/xem private_key được)");
            }
            if (connection.ProjectCode.Length > 0 && !string.Equals(connection.ProjectCode, maDa, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"appSettings:projectCode trong folder là '{connection.ProjectCode}', khác mã dự án");
            }
            return new FolderCheck(true, notes.Count == 0 ? "Kết nối được, DB đã chạy sql/009." : "Kết nối được; lưu ý: " + string.Join("; ", notes) + ".");
        }
    }
}
