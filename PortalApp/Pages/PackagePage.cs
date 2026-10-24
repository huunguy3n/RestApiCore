using System.Text;
using Business;
using PortalApp.Core;
using PortalApp.Ui;

namespace PortalApp.Pages
{
    /// <summary>
    /// Áp gói form (tải từ WebPortal) lên App_Data\Form của folder này. Người áp nhập SHA-256 manifest lấy từ màn Gói của portal (ngoài băng):
    /// gói bị thay trên đường đi sẽ không khớp. Mặc định kiểm thử; áp thật thì bản cũ sao lưu vào _backup, WebApi tự nạp lại form.
    /// Xuất file kết quả để nhập ngược về portal
    /// </summary>
    public sealed class PackagePage : PageBase
    {
        private readonly TextBox package = Input(), sha = Input();
        private readonly AppButton browse = new("Chọn gói .zip", Theme.Icon.Folder, ButtonStyle.Secondary);
        private readonly AppButton verify = new("Kiểm thử", Theme.Icon.Check, ButtonStyle.Secondary);
        private readonly AppButton apply = new("Áp gói", Theme.Icon.Play, ButtonStyle.Danger);
        private readonly DataGridView result = Grid(220);

        public PackagePage(LocalProject project, ILogSink log)
            : base(project, log, Theme.Icon.Upload, "Áp gói form", "Gói DEV → PROD do WebPortal đóng (mọi form đang hiệu lực). Một form hỏng thì không ghi gì.")
        {
            TableLayoutPanel input = AddSection("Gói");
            AddRow(input, "File gói", package);
            AddRow(input, "SHA-256 manifest (từ portal)", sha);
            AddWide(input, Buttons(browse, verify, apply));
            TableLayoutPanel output = AddSection("Kết quả", "Sau mỗi lần chạy, PortalApp hỏi nơi lưu file kết quả JSON: nhập file đó ở WebPortal → Gói DEV→PROD để ghi nhật ký đồng bộ.");
            AddWide(output, result);

            browse.Click += (_, _) =>
            {
                using OpenFileDialog dialog = new() { Filter = "Gói form|*.zip" };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    package.Text = dialog.FileName;
                }
            };
            verify.Click += async (_, _) => await RunAsync(verify, () => RunPackageAsync(false));
            apply.Click += async (_, _) => await RunAsync(apply, () => RunPackageAsync(true));
        }

        private async Task RunPackageAsync(bool write)
        {
            if (!File.Exists(package.Text) || sha.Text.Trim().Length != 64)
            {
                Log.Write(LogLevel.Warning, "Chọn file gói và nhập SHA-256 manifest (64 ký tự hex) lấy ở màn Gói của portal.");
                return;
            }
            if (new FileInfo(package.Text).Length > FormPackage.MaxTotalBytes)
            {
                Log.Write(LogLevel.Error, "File gói quá lớn.");
                return;
            }
            if (!Directory.Exists(Project.FormDirectory))
            {
                Log.Write(LogLevel.Error, $"Không có {Project.FormDirectory}.");
                return;
            }
            if (write && !Confirm("Áp gói", $"Ghi form của gói vào {Project.FormDirectory}? Bản cũ được sao lưu vào _backup.", "Áp gói"))
            {
                return;
            }

            ApplyResult outcome;
            try
            {
                byte[] zip = await File.ReadAllBytesAsync(package.Text);
                outcome = FormPackageApplier.Run(zip, sha.Text.Trim(), Project.FormDirectory, write, "PROD", LocalProject.WindowsUser, TimeProvider.System);
            }
            catch (FormPackageException ex)
            {
                Log.Write(LogLevel.Error, $"{ex.Code}: {ex.Message}");
                return;
            }
            result.DataSource = outcome.Files.Select(f => new { Form = f.Name, Revision = f.Revision, KetQua = f.Outcome, GhiChu = f.Message ?? "" }).ToList();
            Log.Write(outcome.Success ? LogLevel.Success : LogLevel.Error,
                $"Gói #{outcome.PackageId} ({outcome.Files.Count} form): {(outcome.Applied ? "đã áp" : "kiểm thử")} {(outcome.Success ? "đạt" : "KHÔNG đạt")}.");
            await Project.AuditAsync(write ? "package.apply" : "package.verify", $"#{outcome.PackageId}", outcome.Success ? "SUCCESS" : "FAILED");

            using SaveFileDialog save = new() { FileName = $"{(outcome.Applied ? "apply" : "verify")}-result-{outcome.PackageId}.json", Filter = "JSON|*.json" };
            if (save.ShowDialog(this) == DialogResult.OK)
            {
                await File.WriteAllTextAsync(save.FileName, FormPackageApplier.ResultToJson(outcome), new UTF8Encoding(false));
                Log.Write(LogLevel.Info, $"Đã lưu kết quả {save.FileName}.");
            }
        }
    }
}
