using System.Reflection;
using PortalApp.Core;
using PortalApp.Pages;
using PortalApp.Ui;

namespace PortalApp
{
    /// <summary>
    /// Cửa sổ chính: sidebar trái, vùng trang ở giữa, nhật ký thực thi ở dưới.
    /// Chưa có license hợp lệ cho máy này (hoặc nhị phân bị sửa) thì chỉ Tổng quan, Cài đặt (cấu hình) và License dùng được
    /// </summary>
    public sealed class MainForm : Form
    {
        private static readonly HashSet<string> OpenWithoutLicense = ["home", "setup", "license"];

        private readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
        private readonly ExecutionLogPanel logPanel = new() { Dock = DockStyle.Bottom };
        private readonly SidebarNavigation sidebar;
        private readonly Dictionary<string, PageBase> pages = [];
        private readonly LocalProject project;
        private LicenseStatus license;

        public MainForm(LocalProject project)
        {
            this.project = project;
            license = LicenseStatus.Check(project);
            Text = $"Fast PortalApp — {(project.ProjectCode.Length > 0 ? project.ProjectCode : "chưa cấu hình")}";
            Font = Theme.Body();
            BackColor = Theme.Background;
            ClientSize = new Size(1240, 840);
            MinimumSize = new Size(960, 640);
            StartPosition = FormStartPosition.CenterScreen;

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
            sidebar = new SidebarNavigation(
            [
                new("home", "Tổng quan", Theme.Icon.Home),
                new("setup", "Cài đặt", Theme.Icon.Database),
                new("license", "License", Theme.Icon.Key),
                new("users", "User Api", Theme.Icon.User),
                new("logs", "Log & hiệu suất", Theme.Icon.Doc),
                new("package", "Áp gói form", Theme.Icon.Upload),
                new("upgrade", "Nâng form v2", Theme.Icon.Rekey)
            ], "PortalApp", version);

            pages["home"] = new HomePage(project, logPanel);
            pages["setup"] = new SetupPage(project, logPanel, () => license.Unlocked);
            pages["license"] = new LicensePage(project, logPanel, status => license = status);
            pages["users"] = new UsersPage(project, logPanel);
            pages["logs"] = new LogsPage(project, logPanel);
            pages["package"] = new PackagePage(project, logPanel);
            pages["upgrade"] = new UpgradePage(project, logPanel);

            // Thứ tự Add quyết định vùng Fill: host thêm đầu tiên, rồi log và sidebar chiếm cạnh
            Controls.Add(host);
            Controls.Add(logPanel);
            Controls.Add(sidebar);

            sidebar.Navigated += async (_, key) => await ShowAsync(key);
            if (license.IntegrityBroken)
            {
                logPanel.Write(LogLevel.Error, "Nhị phân trong folder đã bị thay đổi so với manifest toàn vẹn: PortalApp chỉ hiện trạng thái. Cài lại từ gói phát hành gốc.");
            }
            else if (!license.Unlocked)
            {
                logPanel.Write(LogLevel.Warning, "Chưa có license hợp lệ cho máy này: chỉ Cài đặt (cấu hình) và License dùng được.");
            }
            sidebar.Select("home");
        }

        private async Task ShowAsync(string key)
        {
            if (license.IntegrityBroken && key != "home" || !license.Unlocked && !OpenWithoutLicense.Contains(key))
            {
                logPanel.Write(LogLevel.Warning, license.IntegrityBroken ? "Nhị phân bị thay đổi: không mở được chức năng." : "Cần license hợp lệ trước (trang License).");
                if (key != "license" && !license.IntegrityBroken)
                {
                    sidebar.Select("license");
                }
                return;
            }
            foreach (Control page in pages.Values)
            {
                page.Visible = false;
            }
            PageBase current = pages[key];
            if (current.Parent != host)
            {
                current.Dock = DockStyle.Fill;
                host.Controls.Add(current);
            }
            current.Visible = true;
            current.Focus();
            try
            {
                await current.RefreshAsync();
            }
            catch (Exception ex)
            {
                logPanel.Write(LogLevel.Error, ex is Microsoft.Data.SqlClient.SqlException sql ? $"Lỗi SQL {sql.Number}: {sql.Message}" : ex.Message);
            }
        }
    }
}
