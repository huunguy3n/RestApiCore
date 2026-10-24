using System.Security.Cryptography;
using DataAccess;
using PortalApp.Core;
using PortalApp.Ui;

namespace PortalApp.Pages
{
    /// <summary>
    /// User Api tại PROD: ghi thẳng restApiUser / restApiAllowForm / restApiAllowIp của DB dự án (PROD không kết nối WebPortal).
    /// Quyền: có khai = đúng danh sách, rỗng = tất cả. Cấp/xoay private_key (hiện đúng một lần), thu hồi token
    /// </summary>
    public sealed class UsersPage : PageBase
    {
        private readonly DataGridView users = Grid(240);
        private readonly TextBox name = Input();
        private readonly ComboBox nameErp = new() { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        private readonly CheckBox active = new() { Text = "Hoạt động (bỏ chọn = khóa, thu hồi mọi token)", AutoSize = true, Checked = true };
        private readonly TextBox forms = Input(multiline: true, height: 80), ips = Input(multiline: true, height: 60);
        private readonly AppButton saveUser = new("Lưu user", Theme.Icon.Check, ButtonStyle.Primary);
        private readonly AppButton issueKey = new("Cấp / xoay private_key", Theme.Icon.Key, ButtonStyle.Secondary);
        private readonly AppButton revoke = new("Thu hồi mọi token", Theme.Icon.Delete, ButtonStyle.Danger);
        private readonly AppButton reload = new("Làm mới", Theme.Icon.Refresh, ButtonStyle.Secondary);
        private IReadOnlyList<ProjectApiUser> current = [];

        public UsersPage(LocalProject project, ILogSink log)
            : base(project, log, Theme.Icon.User, "User Api", "Tạo/sửa user Api map theo NSD FBO, cấp quyền form/IP, cấp private_key, thu hồi token. Mọi thao tác ghi restApiAudit theo Windows user.")
        {
            TableLayoutPanel list = AddSection("Danh sách");
            AddWide(list, users);
            AddWide(list, Buttons(reload));

            TableLayoutPanel edit = AddSection("Tạo / sửa", "Chọn một dòng để sửa, hoặc nhập tên mới để tạo. Form: tên file XML không đuôi, cách nhau dấu phẩy hoặc xuống dòng. IP: mỗi dòng một IP, \"255\" là wildcard.");
            AddRow(edit, "Tên đăng nhập (restApiUser.name)", name);
            AddRow(edit, "NSD FBO (name_erp)", nameErp);
            AddRow(edit, "", active);
            AddRow(edit, "Form được phép (trống = TẤT CẢ)", forms);
            AddRow(edit, "IP được phép (trống = mọi IP)", ips);
            AddWide(edit, Buttons(saveUser, issueKey, revoke));

            users.SelectionChanged += (_, _) => Fill();
            reload.Click += async (_, _) => await RunAsync(reload, RefreshAsync);
            saveUser.Click += async (_, _) => await RunAsync(saveUser, SaveAsync);
            issueKey.Click += async (_, _) => await RunAsync(issueKey, IssueKeyAsync);
            revoke.Click += async (_, _) => await RunAsync(revoke, RevokeAsync);
        }

        public override async Task RefreshAsync()
        {
            ProjectAdminRepository admin = Project.Admin();
            current = await admin.ListUsersAsync();
            users.DataSource = current.Select(u => new
            {
                Ten = u.Name, NameErp = u.NameErp, TrangThai = u.Active ? "Hoạt động" : "Khóa",
                Form = u.Forms.Count == 0 ? "tất cả" : string.Join(", ", u.Forms),
                IP = u.Ips.Count == 0 ? "mọi IP" : string.Join(", ", u.Ips),
                Khoa = u.HasKey ? u.KeyIssuedAt?.ToString("yyyy-MM-dd HH:mm") ?? "có" : "chưa cấp"
            }).ToList();
            string selected = nameErp.Text;
            nameErp.Items.Clear();
            nameErp.Items.AddRange((await admin.ListErpUsersAsync()).Where(e => e.Active).Select(e => (object)e.Name).ToArray());
            nameErp.Text = selected;
        }

        private void Fill()
        {
            if (users.CurrentRow?.Index is int index && index >= 0 && index < current.Count)
            {
                ProjectApiUser user = current[index];
                name.Text = user.Name;
                nameErp.Text = user.NameErp;
                active.Checked = user.Active;
                forms.Text = string.Join(Environment.NewLine, user.Forms);
                ips.Text = string.Join(Environment.NewLine, user.Ips);
            }
        }

        private async Task SaveAsync()
        {
            string userName = name.Text.Trim();
            if (userName.Length is < 3 or > 32 || !userName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '@' or '-'))
            {
                Log.Write(LogLevel.Warning, "Tên đăng nhập 3-32 ký tự: chữ, số, . _ @ -");
                return;
            }
            ErpUser? erp = await Project.Admin().FindErpUserAsync(nameErp.Text.Trim());
            if (erp is not { Active: true })
            {
                Log.Write(LogLevel.Warning, "Không có NSD FBO đang hoạt động với tên này.");
                return;
            }
            List<string> formList = forms.Text.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (formList.Any(f => !Business.FormDefinitionValidator.IsValidName(f)))
            {
                Log.Write(LogLevel.Warning, "Tên form không hợp lệ (tên file XML không đuôi, vd getCustomer).");
                return;
            }
            List<(string Ip, string? Description)> ipList = ips.Text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(' ', 2, StringSplitOptions.TrimEntries)).Select(p => (p[0], p.Length > 1 ? p[1] : (string?)null)).ToList();
            if (ipList.Any(i => i.Ip.Length > 45 || !i.Ip.All(c => char.IsAsciiHexDigit(c) || c is '.' or ':')))
            {
                Log.Write(LogLevel.Warning, "IP không hợp lệ.");
                return;
            }
            if (formList.Count == 0 && !Confirm("Cấp mọi form", $"Danh sách form trống nghĩa là {userName} được gọi TẤT CẢ form của dự án. Đồng ý?", "Đồng ý"))
            {
                return;
            }
            await Project.Admin().UpsertUserAsync(new ApiUserSpec(userName, erp.Name, active.Checked, formList, ipList));
            await Project.AuditAsync("apiuser.save", userName, "SUCCESS",
                $"name_erp {erp.Name}; {(active.Checked ? "hoạt động" : "khóa")}; form: {(formList.Count == 0 ? "tất cả" : string.Join(",", formList))}");
            Log.Write(LogLevel.Success, $"Đã lưu {userName}. WebApi áp dụng sau tối đa userCacheSeconds.");
            await RefreshAsync();
        }

        private async Task IssueKeyAsync()
        {
            string userName = name.Text.Trim();
            ProjectApiUser? user = current.FirstOrDefault(u => string.Equals(u.Name, userName, StringComparison.OrdinalIgnoreCase));
            if (user == null)
            {
                Log.Write(LogLevel.Warning, "Lưu user trước khi cấp khóa.");
                return;
            }
            Security.PrivateKeyProtector? protector = Project.Protector();
            if (protector == null)
            {
                Log.Write(LogLevel.Error, "appsettings.Local.json thiếu Security:KeyEncryptionKey hợp lệ.");
                return;
            }
            if (user.HasKey && !Confirm("Xoay private_key", $"{user.Name} đã có khóa. Khóa cũ hết hiệu lực ngay, đối tác phải cập nhật. Tiếp tục?", "Xoay khóa"))
            {
                return;
            }
            byte[]? key = await Project.Admin().IssueKeyAsync(user.Name, protector);
            if (key == null)
            {
                Log.Write(LogLevel.Error, "Không tìm thấy user trên DB.");
                return;
            }
            string base64 = Convert.ToBase64String(key);
            CryptographicOperations.ZeroMemory(key);
            await Project.AuditAsync("key.issue", user.Name, "SUCCESS");
            // Khóa chỉ hiện trong hộp thoại này, không ghi vào nhật ký
            KeyDialog.Show(this, user.Name, base64);
            await RefreshAsync();
        }

        private async Task RevokeAsync()
        {
            string userName = name.Text.Trim();
            if (userName.Length == 0 || !Confirm("Thu hồi token", $"Mọi JWT đã cấp cho {userName} hết hiệu lực; đối tác phải lấy token mới.", "Thu hồi"))
            {
                return;
            }
            bool done = await Project.Admin().RevokeTokensAsync(userName);
            await Project.AuditAsync("token.revoke", userName, done ? "SUCCESS" : "REJECTED");
            Log.Write(done ? LogLevel.Success : LogLevel.Warning, done ? $"Đã thu hồi mọi token của {userName}." : "Không tìm thấy user.");
            await RefreshAsync();
        }
    }

    /// <summary>Hiện private_key đúng một lần, có nút sao chép; đóng là mất</summary>
    internal static class KeyDialog
    {
        public static void Show(IWin32Window owner, string userName, string key)
        {
            using Form dialog = new()
            {
                Text = "private_key của " + userName,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(560, 190),
                MaximizeBox = false,
                MinimizeBox = false,
                Font = Theme.Body()
            };
            Label note = new() { Text = "Chỉ hiện một lần. Giao cho đối tác qua kênh an toàn (không email/chat rõ).", AutoSize = true, Left = 16, Top = 16, ForeColor = Theme.Warning };
            TextBox value = new() { Text = key, ReadOnly = true, Font = Theme.Mono(11f), Left = 16, Top = 50, Width = 528 };
            Button copy = new() { Text = "Sao chép", Left = 16, Top = 100, Width = 120, Height = 34 };
            Button close = new() { Text = "Đóng", Left = 424, Top = 100, Width = 120, Height = 34, DialogResult = DialogResult.OK };
            copy.Click += (_, _) => Clipboard.SetText(key);
            dialog.Controls.AddRange([note, value, copy, close]);
            dialog.AcceptButton = close;
            dialog.ShowDialog(owner);
        }
    }
}
