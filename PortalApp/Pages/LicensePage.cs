using System.Text;
using PortalApp.Core;
using PortalApp.Ui;
using Security.Licensing;

namespace PortalApp.Pages
{
    /// <summary>
    /// License theo máy: tạo file yêu cầu (mã dự án + MAC), mang lên WebPortal để nhận restApi.lic, nạp vào folder restApi.
    /// Chưa hợp lệ thì chỉ trang License và Cài đặt dùng được
    /// </summary>
    public sealed class LicensePage : PageBase
    {
        private readonly Label status = Info();
        private readonly Label macs = Info();
        private readonly AppButton request = new("Tạo file yêu cầu license", Theme.Icon.File, ButtonStyle.Primary);
        private readonly AppButton load = new("Nạp file license (restApi.lic)", Theme.Icon.Upload, ButtonStyle.Secondary);
        private readonly Action<LicenseStatus> changed;

        public LicensePage(LocalProject project, ILogSink log, Action<LicenseStatus> changed)
            : base(project, log, Theme.Icon.Key, "License", "License gắn mã dự án + MAC của máy này, do WebPortal cấp. Chép folder sang máy khác thì PortalApp không mở được.")
        {
            this.changed = changed;
            TableLayoutPanel state = AddSection("Trạng thái");
            AddWide(state, status);
            AddRow(state, "MAC của máy này", macs);

            TableLayoutPanel steps = AddSection("Cấp license",
                "1. Tạo file yêu cầu (không chứa bí mật). 2. Mang file lên WebPortal → /<mã dự án>/licenses (Admin dự án hoặc SA) để nhận restApi.lic. 3. Nạp file vào đây.");
            AddWide(steps, Buttons(request, load));

            request.Click += async (_, _) => await RunAsync(request, CreateRequestAsync);
            load.Click += async (_, _) => await RunAsync(load, LoadLicenseAsync);
        }

        public override Task RefreshAsync()
        {
            LicenseStatus current = LicenseStatus.Check(Project);
            status.Text = (Project.ProjectCode.Length == 0 ? "Chưa khai mã dự án (vào Cài đặt → Cấu hình trước). " : "") + current.Describe();
            status.ForeColor = current.Unlocked ? Theme.Success : Theme.Error;
            macs.Text = string.Join(", ", MachineFingerprint.GetMacs());
            return Task.CompletedTask;
        }

        private async Task CreateRequestAsync()
        {
            if (Project.ProjectCode.Length == 0)
            {
                Log.Write(LogLevel.Warning, "Chưa khai mã dự án: vào Cài đặt → Cấu hình kết nối trước.");
                return;
            }
            IReadOnlyList<string> list = MachineFingerprint.GetMacs();
            if (list.Count == 0)
            {
                Log.Write(LogLevel.Error, "Không tìm thấy card mạng vật lý nào để gắn license.");
                return;
            }
            LicenseRequest requestFile = LicenseRequest.Create(Project.ProjectCode, list, LocalProject.MachineName,
                typeof(LicensePage).Assembly.GetName().Version?.ToString(3) ?? "", DateTimeOffset.UtcNow);
            using SaveFileDialog dialog = new() { FileName = "license-request.json", Filter = "JSON|*.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            await File.WriteAllTextAsync(dialog.FileName, requestFile.ToJson(), new UTF8Encoding(false));
            Log.Write(LogLevel.Success, $"Đã tạo {dialog.FileName}. Mang file lên WebPortal của dự án {Project.ProjectCode} để cấp license.");
        }

        private async Task LoadLicenseAsync()
        {
            using OpenFileDialog dialog = new() { Filter = "License|*.lic;*.json|Tất cả|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            if (new FileInfo(dialog.FileName).Length > 64 * 1024)
            {
                Log.Write(LogLevel.Error, "File license quá lớn.");
                return;
            }
            string text = await File.ReadAllTextAsync(dialog.FileName);
            LicenseCheck check = new LicenseVerifier(TrustedLicenseKeys.Load()).Verify(text, Project.ProjectCode, MachineFingerprint.GetMacs(), DateTimeOffset.UtcNow);
            if (!check.IsValid)
            {
                Log.Write(LogLevel.Error, "License không dùng được cho máy này: " + LicenseCodes.Describe(check.Code));
                return;
            }
            string target = Path.Combine(Project.Folder, LicenseRules.FileName);
            await File.WriteAllTextAsync(target, text, new UTF8Encoding(false));
            LicensePayload payload = check.Payload!;
            try
            {
                await Project.Admin().RecordLicenseAsync(payload.LicenseId, payload.ProjectCode, string.Join(",", payload.Macs), payload.MachineName,
                    payload.ExpiresAt?.LocalDateTime, LocalProject.WindowsUser);
                await Project.AuditAsync("license.activate", payload.LicenseId.ToString(), "SUCCESS");
            }
            catch (Exception ex) when (ex is Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                Log.Write(LogLevel.Warning, "Đã nạp license nhưng chưa ghi được restApiLicense trên DB (chạy SQL ở Cài đặt rồi nạp lại để tra soát).");
            }
            Log.Write(LogLevel.Success, $"Đã nạp license {payload.LicenseId}. Khởi động lại site IIS để WebApi đọc license mới.");
            await RefreshAsync();
            changed(LicenseStatus.Check(Project));
        }
    }
}
