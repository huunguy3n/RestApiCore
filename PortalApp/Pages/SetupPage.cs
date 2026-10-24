using DataAccess;
using PortalApp.Core;
using PortalApp.Ui;

namespace PortalApp.Pages
{
    /// <summary>
    /// Cài đặt PROD bằng giao diện (thay CLI/checklist tay): cấu hình kết nối + sinh bí mật, chạy script SQL theo thứ tự,
    /// dựng site IIS, khóa quyền folder. Bước SQL/IIS chỉ mở khi license hợp lệ
    /// </summary>
    public sealed class SetupPage : PageBase
    {
        private readonly TextBox server = Input(), database = Input(), user = Input(), password = Input(password: true), projectCode = Input();
        private readonly CheckBox windowsAuth = new() { Text = "Đăng nhập Windows (Integrated Security)", AutoSize = true };
        private readonly ComboBox environment = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label configState = Info();
        private readonly AppButton test = new("Thử kết nối", Theme.Icon.Refresh, ButtonStyle.Secondary);
        private readonly AppButton save = new("Lưu cấu hình", Theme.Icon.Check, ButtonStyle.Primary);

        private readonly DataGridView scripts = Grid(220);
        private readonly AppButton refreshScripts = new("Làm mới", Theme.Icon.Refresh, ButtonStyle.Secondary);
        private readonly AppButton runPending = new("Chạy script chưa áp", Theme.Icon.Play, ButtonStyle.Primary);

        private readonly TextBox site = Input("FastRestApi"), pool = Input("FastRestApi"), bindings = Input("http/*:8080:");
        private readonly AppButton iis = new("Tạo / cập nhật site IIS", Theme.Icon.Play, ButtonStyle.Primary);
        private readonly AppButton acl = new("Khóa quyền folder", Theme.Icon.Lock, ButtonStyle.Danger);

        private readonly Func<bool> unlocked;

        public SetupPage(LocalProject project, ILogSink log, Func<bool> unlocked)
            : base(project, log, Theme.Icon.Database, "Cài đặt", $"Folder: {project.Folder}. Cấu hình đọc/ghi tại appsettings.Local.json của folder này.")
        {
            this.unlocked = unlocked;
            environment.Items.AddRange(["PROD", "DEV"]);
            environment.SelectedIndex = 0;

            TableLayoutPanel config = AddSection("1. Cấu hình kết nối",
                "Chỉ cần làm một lần. Lưu sẽ sinh secretKey và KeyEncryptionKey nếu chưa có (không sinh lại khi đã có, để không mất private_key đã cấp).");
            AddWide(config, configState);
            AddRow(config, "SQL Server", server);
            AddRow(config, "Database app (FBO)", database);
            AddRow(config, "", windowsAuth);
            AddRow(config, "Tài khoản SQL", user);
            AddRow(config, "Mật khẩu SQL", password);
            AddRow(config, "Mã dự án (nbdmda)", projectCode);
            AddRow(config, "Môi trường", environment);
            AddWide(config, Buttons(test, save));

            TableLayoutPanel sql = AddSection("2. Script SQL", "Chạy sql/NNN_*.sql trong folder theo thứ tự, ghi restApiSetupLog. Script đều chạy lại được; CHANGED = nội dung khác lần đã áp.");
            AddWide(sql, scripts);
            AddWide(sql, Buttons(refreshScripts, runPending));

            TableLayoutPanel web = AddSection("3. IIS và quyền folder",
                "App Pool \"No Managed Code\" trỏ vào folder này. Khóa quyền: chỉ Administrators, SYSTEM và App Pool đọc được folder (PortalApp không có đăng nhập).");
            AddRow(web, "Tên site", site);
            AddRow(web, "Tên App Pool", pool);
            AddRow(web, "Binding (vd https/*:443: hoặc http/*:8080:)", bindings);
            AddWide(web, Buttons(iis, acl));

            windowsAuth.CheckedChanged += (_, _) => user.Enabled = password.Enabled = !windowsAuth.Checked;
            test.Click += async (_, _) => await RunAsync(test, TestAsync);
            save.Click += async (_, _) => await RunAsync(save, SaveAsync);
            refreshScripts.Click += async (_, _) => await RunAsync(refreshScripts, LoadScriptsAsync);
            runPending.Click += async (_, _) => await RunAsync(runPending, RunScriptsAsync);
            iis.Click += async (_, _) => await RunAsync(iis, IisAsync);
            acl.Click += async (_, _) => await RunAsync(acl, AclAsync);
        }

        public override async Task RefreshAsync()
        {
            configState.Text = Project.HasLocalSettings
                ? $"Đã có cấu hình: dự án {Project.ProjectCode}, môi trường {Project.Environment}. Nhập lại các ô dưới nếu muốn đổi kết nối."
                : "Chưa có appsettings.Local.json: nhập kết nối để tạo.";
            if (projectCode.Text.Length == 0)
            {
                projectCode.Text = Project.ProjectCode;
            }
            if (Project.IsConfigured)
            {
                await LoadScriptsAsync();
            }
        }

        private ConnectionInput Connection() => new(server.Text, database.Text, windowsAuth.Checked, user.Text, password.Text);

        private bool ValidInput()
        {
            if (server.Text.Trim().Length == 0 || database.Text.Trim().Length == 0 || !windowsAuth.Checked && user.Text.Trim().Length == 0)
            {
                Log.Write(LogLevel.Warning, "Nhập SQL Server, database và tài khoản (hoặc chọn đăng nhập Windows).");
                return false;
            }
            if (!Security.Licensing.LicenseRules.IsValidProjectCode(projectCode.Text.Trim()))
            {
                Log.Write(LogLevel.Warning, "Mã dự án chỉ gồm chữ, số, _ và -, tối đa 32 ký tự.");
                return false;
            }
            return true;
        }

        private async Task TestAsync()
        {
            if (!ValidInput())
            {
                return;
            }
            string? error = await SetupService.TestConnectionAsync(SetupService.BuildConnectionString(Connection()));
            Log.Write(error == null ? LogLevel.Success : LogLevel.Error, error ?? "Kết nối được, DB có vsysuserinfo.");
        }

        private async Task SaveAsync()
        {
            if (!ValidInput())
            {
                return;
            }
            string connection = SetupService.BuildConnectionString(Connection());
            string? error = await SetupService.TestConnectionAsync(connection);
            if (error != null)
            {
                Log.Write(LogLevel.Error, error + " Chưa lưu.");
                return;
            }
            SetupService.WriteLocalSettings(Project, connection, projectCode.Text.Trim(), environment.SelectedItem?.ToString() ?? "PROD");
            password.Text = "";
            Log.Write(LogLevel.Success, "Đã lưu appsettings.Local.json. Bước tiếp: License, rồi chạy script SQL.");
            await RefreshAsync();
        }

        private async Task LoadScriptsAsync()
        {
            IReadOnlyList<string> files = SqlScriptRunner.FindScripts(Project.SqlDirectory);
            if (files.Count == 0)
            {
                Log.Write(LogLevel.Warning, $"Không có script trong {Project.SqlDirectory}.");
                scripts.DataSource = null;
                return;
            }
            IReadOnlyList<ScriptStatus> statuses = await SqlScriptRunner.StatusAsync(Project.Database(), files);
            scripts.DataSource = statuses.Select(s => new { Script = s.Script, TrangThai = s.State, ApLuc = s.AppliedAt?.ToString("yyyy-MM-dd HH:mm") ?? "" }).ToList();
        }

        private async Task RunScriptsAsync()
        {
            if (!RequireLicense() || !Project.IsConfigured)
            {
                return;
            }
            IReadOnlyList<string> files = SqlScriptRunner.FindScripts(Project.SqlDirectory);
            if (!Confirm("Chạy script SQL", $"Chạy các script chưa áp trên DB của dự án {Project.ProjectCode}?", "Chạy"))
            {
                return;
            }
            Progress<string> progress = new(message => Log.Write(LogLevel.Info, message));
            string version = typeof(SetupPage).Assembly.GetName().Version?.ToString(3) ?? "";
            IReadOnlyList<ScriptRunResult> results = await SqlScriptRunner.RunAsync(Project.Database(), files, version, LocalProject.WindowsUser, true, progress);
            foreach (ScriptRunResult result in results.Where(r => r.Applied || r.Error != null))
            {
                Log.Write(result.Error == null ? LogLevel.Success : LogLevel.Error, result.Error == null ? $"Đã áp {result.Script}" : $"{result.Script}: {result.Error}");
            }
            bool ok = results.All(r => r.Error == null);
            await Project.AuditAsync("setup.sql", $"{results.Count(r => r.Applied)} script", ok ? "SUCCESS" : "FAILED");
            await LoadScriptsAsync();
        }

        private async Task IisAsync()
        {
            if (!RequireLicense())
            {
                return;
            }
            if (!SetupService.IisAvailable)
            {
                Log.Write(LogLevel.Error, "Máy chưa cài IIS (không có appcmd.exe).");
                return;
            }
            string preview = string.Join(Environment.NewLine, SetupService.IisCommands(site.Text.Trim(), pool.Text.Trim(), Project.Folder, bindings.Text.Trim())
                .Select(a => "appcmd " + string.Join(' ', a)));
            if (!Confirm("Dựng site IIS", preview, "Chạy"))
            {
                return;
            }
            foreach (CommandResult result in await SetupService.SetupIisAsync(site.Text.Trim(), pool.Text.Trim(), Project.Folder, bindings.Text.Trim()))
            {
                Log.Write(result.Ok ? LogLevel.Success : LogLevel.Warning, result.Output);
            }
            await Project.AuditAsync("setup.iis", site.Text.Trim(), "SUCCESS", bindings.Text.Trim());
        }

        private async Task AclAsync()
        {
            if (!RequireLicense())
            {
                return;
            }
            if (!Confirm("Khóa quyền folder", $"Bỏ kế thừa quyền của {Project.Folder}: chỉ Administrators, SYSTEM và IIS AppPool\\{pool.Text.Trim()} còn truy cập. Tiếp tục?", "Khóa"))
            {
                return;
            }
            foreach (CommandResult result in await SetupService.LockFolderAsync(Project.Folder, pool.Text.Trim()))
            {
                Log.Write(result.Ok ? LogLevel.Success : LogLevel.Error, result.Output);
            }
            await Project.AuditAsync("setup.acl", Project.Folder, "SUCCESS");
        }

        private bool RequireLicense()
        {
            if (unlocked())
            {
                return true;
            }
            Log.Write(LogLevel.Warning, "Cần license hợp lệ cho máy này trước (trang License).");
            return false;
        }
    }
}
