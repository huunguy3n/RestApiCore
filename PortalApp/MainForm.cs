using System.Reflection;
using PortalApp.Core;
using PortalApp.Pages;
using PortalApp.Ui;

namespace PortalApp
{
    /// <summary>Cửa sổ chính: sidebar trái, vùng trang ở giữa, nhật ký thực thi ở dưới. Chỉ lo điều hướng và ghép các phần.</summary>
    public sealed class MainForm : Form
    {
        private const string HomeKey = "home";

        private static readonly Dictionary<string, string> Icons = new()
        {
            ["create-user"] = Theme.Icon.AddUser,
            ["set-password"] = Theme.Icon.Lock,
            ["issue-key"] = Theme.Icon.Key,
            ["rekey-passwords"] = Theme.Icon.Rekey,
            ["upgrade"] = Theme.Icon.Upload
        };

        private readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
        private readonly ExecutionLogPanel logPanel = new() { Dock = DockStyle.Bottom };
        private readonly SidebarNavigation sidebar;
        private readonly Dictionary<string, Control> pages = [];
        private readonly CommandRunner runner = new();
        private readonly SettingsStore settings = new();

        public MainForm()
        {
            Text = "Fast.Api — Công cụ quản trị";
            Font = Theme.Body();
            BackColor = Theme.Background;
            ClientSize = new Size(1180, 800);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterScreen;

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
            var entries = new List<NavEntry> { new(HomeKey, "Trang chủ", Theme.Icon.Home) };
            entries.AddRange(CommandCatalog.All.Select(c => new NavEntry(c.Name, c.Title, Icons.GetValueOrDefault(c.Name, Theme.Icon.Doc))));
            sidebar = new SidebarNavigation(entries, "Fast.Api", version);

            pages[HomeKey] = new HomePage(CommandCatalog.All.Select(c => (c, Icons.GetValueOrDefault(c.Name, Theme.Icon.Doc))), key => sidebar.Select(key));
            foreach (CommandDef c in CommandCatalog.All)
            {
                pages[c.Name] = new CommandPage(c, Icons.GetValueOrDefault(c.Name, Theme.Icon.Doc), runner, logPanel, settings,
                    () => logPanel.Expanded = !logPanel.Expanded);
            }

            // Thứ tự Add quyết định vùng Fill: host thêm đầu tiên, rồi log và sidebar chiếm cạnh
            Controls.Add(host);
            Controls.Add(logPanel);
            Controls.Add(sidebar);

            sidebar.Navigated += (_, key) => Show(key);
            logPanel.Write(LogLevel.Info, "Sẵn sàng. Mặc định mọi lệnh chạy ở chế độ kiểm thử (không ghi database).");
            sidebar.Select(HomeKey);
        }

        private void Show(string key)
        {
            foreach (Control page in pages.Values) { page.Visible = false; }
            Control current = pages[key];
            if (current.Parent != host)
            {
                current.Dock = DockStyle.Fill;
                host.Controls.Add(current);
            }
            current.Visible = true;
            current.Focus();
        }
    }
}
