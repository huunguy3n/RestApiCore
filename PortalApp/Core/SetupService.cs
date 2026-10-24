using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Provider;
using Security;

namespace PortalApp.Core
{
    public sealed record ConnectionInput(string Server, string Database, bool WindowsAuth, string User, string Password);

    public sealed record CommandResult(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>
    /// Các bước cài đặt PROD trước đây làm bằng CLI/checklist tay: ghi appsettings.Local.json (sinh bí mật), thử kết nối SQL,
    /// dựng site IIS bằng appcmd, khóa quyền folder bằng icacls. Không bao giờ ghi bí mật ra log
    /// </summary>
    public static class SetupService
    {
        public static string BuildConnectionString(ConnectionInput input)
        {
            SqlConnectionStringBuilder builder = new()
            {
                DataSource = input.Server.Trim(),
                InitialCatalog = input.Database.Trim(),
                ApplicationName = "restApi",
                TrustServerCertificate = true,
                Encrypt = SqlConnectionEncryptOption.Optional
            };
            if (input.WindowsAuth)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = input.User.Trim();
                builder.Password = input.Password;
            }
            return builder.ConnectionString;
        }

        /// <summary>Null nếu kết nối được và DB có vsysuserinfo (DB app FBO); ngược lại là mô tả lỗi không lộ connection string</summary>
        public static async Task<string?> TestConnectionAsync(string connectionString)
        {
            try
            {
                var data = await new SqlDatabase(connectionString, 15).RetrieveAsync(
                    "SELECT CASE WHEN OBJECT_ID('dbo.vsysuserinfo') IS NULL THEN 0 ELSE 1 END AS ok");
                return Convert.ToInt32(data.Tables[0].Rows[0]["ok"]) == 1 ? null : "Kết nối được nhưng DB không có vsysuserinfo (không phải DB app FBO?).";
            }
            catch (DatabaseException ex) when (ex.InnerException is SqlException sql)
            {
                return $"Không kết nối được SQL (lỗi {sql.Number}).";
            }
            catch (SqlException ex)
            {
                return $"Không kết nối được SQL (lỗi {ex.Number}).";
            }
        }

        /// <summary>
        /// Ghi appsettings.Local.json: giữ khóa đã có, chỉ sinh secretKey / KeyEncryptionKey khi chưa có (sinh lại sẽ làm mất private_key đã cấp)
        /// </summary>
        public static void WriteLocalSettings(LocalProject project, string connectionString, string projectCode, string environment)
        {
            string path = Path.Combine(project.Folder, LocalProject.LocalSettingsFile);
            JsonObject root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
            JsonObject app = root["appSettings"] as JsonObject ?? [];
            JsonObject strings = root["connectionStrings"] as JsonObject ?? [];
            JsonObject security = root["Security"] as JsonObject ?? [];

            if (string.IsNullOrEmpty(app["secretKey"]?.GetValue<string>()))
            {
                app["secretKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            }
            if (string.IsNullOrEmpty(security["KeyEncryptionKey"]?.GetValue<string>()))
            {
                security["KeyEncryptionKey"] = PrivateKeyProtector.GenerateKeyEncryptionKey();
            }
            app["projectCode"] = projectCode;
            app["environment"] = environment;
            strings["appConnectionString"] = connectionString;
            root["appSettings"] = app;
            root["connectionStrings"] = strings;
            root["Security"] = security;

            string temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            project.Reload();
        }

        private static string AppCmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");

        public static bool IisAvailable => File.Exists(AppCmd);

        /// <summary>Lệnh appcmd sẽ chạy (hiện cho người cài xem trước). App Pool "No Managed Code" cho ASP.NET Core</summary>
        public static IReadOnlyList<string[]> IisCommands(string siteName, string poolName, string physicalPath, string bindings) =>
        [
            ["add", "apppool", $"/name:{poolName}", "/managedRuntimeVersion:", "/managedPipelineMode:Integrated"],
            ["add", "site", $"/name:{siteName}", $"/physicalPath:{physicalPath}", $"/bindings:{bindings}"],
            ["set", "app", $"{siteName}/", $"/applicationPool:{poolName}"]
        ];

        public static async Task<IReadOnlyList<CommandResult>> SetupIisAsync(string siteName, string poolName, string physicalPath, string bindings)
        {
            List<CommandResult> results = [];
            foreach (string[] args in IisCommands(siteName, poolName, physicalPath, bindings))
            {
                CommandResult result = await RunAsync(AppCmd, args);
                results.Add(result);
                // Đã có app pool / site thì appcmd báo lỗi 183: coi như đã cài, đi tiếp
                if (!result.Ok && !result.Output.Contains("already exists", StringComparison.OrdinalIgnoreCase) && !result.Output.Contains("183", StringComparison.Ordinal))
                {
                    break;
                }
            }
            return results;
        }

        /// <summary>
        /// Khóa folder restApi: bỏ kế thừa, chỉ Administrators (toàn quyền), SYSTEM và App Pool identity (đọc + thực thi; ghi App_Data để FormReloader / spool log)
        /// </summary>
        public static async Task<IReadOnlyList<CommandResult>> LockFolderAsync(string folder, string poolName)
        {
            string icacls = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe");
            return
            [
                await RunAsync(icacls, [folder, "/inheritance:r", "/grant:r", "*S-1-5-32-544:(OI)(CI)F", "/grant:r", "*S-1-5-18:(OI)(CI)F",
                    "/grant:r", $"IIS AppPool\\{poolName}:(OI)(CI)RX", "/T", "/C", "/Q"]),
                await RunAsync(icacls, [Path.Combine(folder, "App_Data"), "/grant", $"IIS AppPool\\{poolName}:(OI)(CI)M", "/T", "/C", "/Q"])
            ];
        }

        private static async Task<CommandResult> RunAsync(string file, IEnumerable<string> args)
        {
            ProcessStartInfo start = new(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            using Process process = Process.Start(start)!;
            string output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new CommandResult(process.ExitCode, $"{Path.GetFileName(file)} {string.Join(' ', start.ArgumentList)}: {output.Trim()}");
        }
    }
}
