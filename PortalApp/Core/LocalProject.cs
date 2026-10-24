using System.Security.Principal;
using DataAccess;
using Microsoft.Extensions.Configuration;
using Provider;
using Security;
using Security.Licensing;

namespace PortalApp.Core
{
    /// <summary>
    /// Folder restApi của dự án mà PortalApp đang nằm trong (cạnh WebApi.dll, appsettings.json). Mọi cấu hình đọc từ chính folder này:
    /// không có ô nhập connection string ngoài bước setup lần đầu. Không có đăng nhập: ai mở được folder trên máy PROD là người quản trị,
    /// nên folder phải được ACL chỉ cho Administrators + App Pool identity; mọi thao tác ghi nhật ký theo Windows user
    /// </summary>
    public sealed class LocalProject
    {
        public const string LocalSettingsFile = "appsettings.Local.json";
        public const string SettingsFile = "appsettings.json";
#if DEBUG
        /// <summary>Chỉ bản DEBUG: chạy PortalApp ngoài folder restApi (máy dev)</summary>
        public const string DebugFolderVariable = "FAST_PORTALAPP_FOLDER";
#endif

        private LocalProject(string folder)
        {
            Folder = folder;
            Reload();
        }

        public string Folder { get; }
        public string ConnectionString { get; private set; } = "";
        public string KeyEncryptionKey { get; private set; } = "";
        public string ProjectCode { get; private set; } = "";
        public string Environment { get; private set; } = "";
        public bool HasLocalSettings => File.Exists(Path.Combine(Folder, LocalSettingsFile));
        public bool IsConfigured => ConnectionString.Length > 0 && ProjectCode.Length > 0;

        public string FormDirectory => Path.Combine(Folder, "App_Data", "Form");
        public string SqlDirectory => Path.Combine(Folder, "sql");

        public static string WindowsUser => WindowsIdentity.GetCurrent().Name;
        public static string MachineName => System.Environment.MachineName;

        public static LocalProject Open()
        {
            string folder = AppContext.BaseDirectory.TrimEnd('\\', '/');
#if DEBUG
            string? debugFolder = System.Environment.GetEnvironmentVariable(DebugFolderVariable);
            if (!string.IsNullOrWhiteSpace(debugFolder) && Directory.Exists(debugFolder))
            {
                folder = debugFolder;
            }
#endif
            return new LocalProject(folder);
        }

        public void Reload()
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(Folder, SettingsFile), optional: true)
                .AddJsonFile(Path.Combine(Folder, LocalSettingsFile), optional: true)
                .Build();
            ConnectionString = config.GetConnectionString("appConnectionString") ?? "";
            KeyEncryptionKey = config["Security:KeyEncryptionKey"] ?? "";
            ProjectCode = config["appSettings:projectCode"] ?? "";
            Environment = config["appSettings:environment"] ?? "";
        }

        public SqlDatabase Database() => new(ConnectionString, 120);

        public ProjectAdminRepository Admin() => new(Database());

        public LogViewRepository Logs() => new(Database());

        /// <summary>Null nếu chưa có KeyEncryptionKey hợp lệ</summary>
        public PrivateKeyProtector? Protector()
        {
            try
            {
                return new PrivateKeyProtector(KeyEncryptionKey);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public Task AuditAsync(string action, string? target, string outcome, string? detail = null) =>
            Admin().WriteAuditAsync(new AuditEntry("PORTALAPP", action, target, outcome, null, detail, WindowsUser, MachineName));
    }

    /// <summary>License + toàn vẹn của bản PortalApp/WebApi trong folder, kiểm lúc mở và sau khi nạp license</summary>
    public sealed class LicenseStatus
    {
        private LicenseStatus(LicenseCheck license, IntegrityResult integrity)
        {
            License = license;
            Integrity = integrity;
        }

        public LicenseCheck License { get; }
        public IntegrityResult Integrity { get; }

        /// <summary>License hợp lệ và nhị phân không bị sửa (thiếu manifest toàn vẹn chỉ cảnh báo)</summary>
        public bool Unlocked => License.IsValid && !IntegrityBroken;
        public bool IntegrityBroken => Integrity.Code is IntegrityManifest.BadSignature or IntegrityManifest.Changed;

        public static LicenseStatus Check(LocalProject project)
        {
            LicenseVerifier verifier = new(TrustedLicenseKeys.Load());
            LicenseCheck license = project.ProjectCode.Length == 0
                ? new LicenseCheck(LicenseCodes.WrongProject)
                : new LicenseGuard(verifier, () => MachineFingerprint.GetMacs(), TimeProvider.System, LicenseGuard.DefaultClockFile).Check(project.Folder, project.ProjectCode);
            return new LicenseStatus(license, IntegrityManifest.Verify(AppContext.BaseDirectory, verifier));
        }

        public string Describe() =>
            $"{LicenseCodes.Describe(License.Code)}"
            + (License.Payload is { } p ? $" (license {p.LicenseId}, máy {p.MachineName}, {(p.ExpiresAt == null ? "không hạn" : "hạn " + p.ExpiresAt.Value.LocalDateTime.ToString("yyyy-MM-dd"))})" : "")
            + Integrity.Code switch
            {
                IntegrityManifest.Valid => " Nhị phân: toàn vẹn.",
                IntegrityManifest.Missing => " Nhị phân: chưa có manifest toàn vẹn (integrity.json/.sig).",
                _ => " Nhị phân BỊ THAY ĐỔI: " + string.Join("; ", Integrity.Problems.Take(3))
            };
    }
}
