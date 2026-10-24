using PortalApp.Core;
using PortalApp.Ui;

namespace PortalApp.Pages
{
    /// <summary>Trạng thái folder restApi trên máy này: cấu hình, license, toàn vẹn nhị phân, kết nối DB</summary>
    public sealed class HomePage : PageBase
    {
        private readonly Label folder = Info(), config = Info(), license = Info(), database = Info();
        private readonly AppButton check = new("Kiểm tra lại", Theme.Icon.Refresh, ButtonStyle.Primary);

        public HomePage(LocalProject project, ILogSink log)
            : base(project, log, Theme.Icon.Home, "Tổng quan", "PortalApp quản trị WebApi của dự án ngay trên máy PROD: không có đăng nhập, quyền truy cập do ACL Windows của folder restApi quyết định.")
        {
            TableLayoutPanel state = AddSection("Trạng thái");
            AddRow(state, "Folder restApi", folder);
            AddRow(state, "Cấu hình", config);
            AddRow(state, "License", license);
            AddRow(state, "Database", database);
            AddWide(state, Buttons(check));
            TableLayoutPanel steps = AddSection("Thứ tự cài đặt lần đầu",
                "1. Cài đặt → Cấu hình kết nối. 2. License → tạo yêu cầu, nạp license. 3. Cài đặt → chạy script SQL, dựng IIS, khóa quyền folder. 4. User Api → tạo user, cấp private_key.");
            check.Click += async (_, _) => await RunAsync(check, RefreshAsync);
        }

        public override async Task RefreshAsync()
        {
            Project.Reload();
            folder.Text = Project.Folder;
            config.Text = Project.IsConfigured ? $"Dự án {Project.ProjectCode}, môi trường {Project.Environment}" : "Chưa cấu hình (Cài đặt → Cấu hình kết nối)";
            LicenseStatus status = LicenseStatus.Check(Project);
            license.Text = status.Describe();
            license.ForeColor = status.Unlocked ? Theme.Success : Theme.Error;
            if (!Project.IsConfigured)
            {
                database.Text = "—";
                return;
            }
            string? schema;
            try
            {
                schema = await Project.Admin().CheckSchemaAsync();
            }
            catch (Exception ex) when (ex is Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException)
            {
                schema = "Không kết nối được SQL.";
            }
            database.Text = schema ?? "Kết nối được, đã chạy sql/009.";
            database.ForeColor = schema == null ? Theme.Success : Theme.Warning;
        }
    }
}
