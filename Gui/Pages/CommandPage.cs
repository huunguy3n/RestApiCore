using Gui.Core;
using Gui.Ui;

namespace Gui.Pages
{
    /// <summary>
    /// Trang chung cho mọi lệnh: sinh form từ <see cref="CommandDef"/>. Trang chỉ điều phối
    /// (kiểm tra → xác nhận → chạy qua <see cref="CommandRunner"/> → báo kết quả); nghiệp vụ nằm ở Cli.
    /// </summary>
    public sealed class CommandPage : UserControl
    {
        private const int Pad = 24;

        private readonly CommandDef command;
        private readonly CommandRunner runner;
        private readonly ILogSink log;
        private readonly SettingsStore settings;
        private readonly Action toggleLog;

        private readonly PageHeader header = new();
        private readonly NotificationBanner banner = new();
        private readonly Card formCard;
        private readonly Card optionsCard;
        private readonly Card previewCard = new(Theme.Icon.Doc, "Dòng lệnh tương đương (mật khẩu không hiển thị)");
        private readonly CommandPreview preview = new();
        private readonly AppButton refreshButton = new("Làm mới", Theme.Icon.Refresh, ButtonStyle.Secondary);
        private readonly AppButton copyButton = new("Chép lệnh CLI", Theme.Icon.Copy, ButtonStyle.Secondary);
        private readonly AppButton logButton = new("Xem log", Theme.Icon.Terminal, ButtonStyle.Secondary);
        private readonly AppButton runButton = new("Thực hiện", Theme.Icon.Play, ButtonStyle.Primary);

        private readonly Dictionary<ParamDef, FormField> fields = [];
        private readonly Dictionary<ParamDef, ToggleOption> toggles = [];
        private readonly ParamDef? applyParam;
        private bool ready;

        public CommandPage(CommandDef command, string icon, CommandRunner runner, ILogSink log, SettingsStore settings, Action toggleLog)
        {
            this.command = command;
            this.runner = runner;
            this.log = log;
            this.settings = settings;
            this.toggleLog = toggleLog;
            applyParam = command.Params.FirstOrDefault(p => p.IsApply);

            BackColor = Theme.Background;
            AutoScroll = true;
            DoubleBuffered = true;
            Name = command.Name;

            header.Set(icon, command.Title, command.Name, command.Description);
            formCard = new Card(icon, "Thông tin cần nhập");
            optionsCard = new Card(Theme.Icon.Key, "Tùy chọn thực thi");

            BuildFields();
            previewCard.Controls.Add(preview);
            Controls.AddRange([header, banner, formCard, optionsCard, previewCard, refreshButton, copyButton, logButton, runButton]);
            optionsCard.Visible = toggles.Count > 0;

            banner.Dismissed += (_, _) => PerformLayout();
            refreshButton.Click += (_, _) => ResetForm();
            copyButton.Click += (_, _) => CopyCommand();
            logButton.Click += (_, _) => toggleLog();
            runButton.Click += async (_, _) => await ExecuteAsync();
            runner.BusyChanged += (_, _) => UpdateBusy();

            LoadSaved();
            RefreshPreview();
            ready = true;
            PerformLayout();
        }

        public void ClearBanner() => banner.Hide2();

        private void BuildFields()
        {
            foreach (ParamDef p in command.Params)
            {
                if (p.Kind == ParamKind.Flag)
                {
                    var t = new ToggleOption(p.IsApply ? Theme.Icon.Database : Theme.Icon.Key, p.Label,
                        p.Help.Length > 0 ? p.Help : "Bật/tắt tùy chọn này", p.IsApply ? OptionTone.Risk : OptionTone.Normal);
                    t.CheckedChanged += (_, _) => RefreshPreview();
                    toggles[p] = t;
                    optionsCard.Controls.Add(t);
                    continue;
                }

                var f = new FormField(p.Label, LeadIcon(p), Placeholder(p), p.Required, p.Kind == ParamKind.Password, p.Help);
                f.TextChangedByUser += (_, _) => RefreshPreview();
                if (p.Kind is ParamKind.File or ParamKind.FileOrFolder) { f.AddTextAction("File…", () => PickFile(p, f)); }
                if (p.Kind is ParamKind.Folder or ParamKind.FileOrFolder) { f.AddTextAction("Thư mục…", () => PickFolder(f)); }
                fields[p] = f;
                formCard.Controls.Add(f);
            }
        }

        private static string LeadIcon(ParamDef p) => p.Kind switch
        {
            ParamKind.Password => Theme.Icon.Lock,
            ParamKind.File => Theme.Icon.File,
            ParamKind.Folder or ParamKind.FileOrFolder => Theme.Icon.Folder,
            _ => Theme.Icon.User
        };

        private static string Placeholder(ParamDef p) => p.Kind switch
        {
            ParamKind.File => "Chọn " + p.Label + "…",
            ParamKind.Folder or ParamKind.FileOrFolder => "Chọn " + p.Label + "…",
            ParamKind.Password => "Nhập " + p.Label,
            _ => "Nhập " + p.Label
        };

        private static void PickFile(ParamDef p, FormField target)
        {
            using var dialog = new OpenFileDialog { Filter = p.Filter, FileName = target.Value };
            if (dialog.ShowDialog() == DialogResult.OK) { target.Value = dialog.FileName; }
        }

        private static void PickFolder(FormField target)
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = target.Value };
            if (dialog.ShowDialog() == DialogResult.OK) { target.Value = dialog.SelectedPath; }
        }

        private string Key(ParamDef p) => command.Name + ":" + p.Label;

        // Cờ --apply không bao giờ được khôi phục từ lần trước, luôn mặc định tắt để không ghi nhầm
        private void LoadSaved()
        {
            foreach (var (p, f) in fields)
            {
                if (p.Kind != ParamKind.Password) { f.Value = settings.Get(Key(p), p.Default); }
            }
            foreach (var (p, t) in toggles) { t.Checked = !p.IsApply && settings.Get(Key(p)) == "1"; }
        }

        private void SaveValues()
        {
            foreach (var (p, f) in fields)
            {
                if (p.Kind != ParamKind.Password) { settings.Set(Key(p), f.Value); }
            }
            foreach (var (p, t) in toggles)
            {
                if (!p.IsApply) { settings.Set(Key(p), t.Checked ? "1" : ""); }
            }
            settings.Save();
        }

        private string ValueOf(ParamDef p) =>
            fields.TryGetValue(p, out FormField? f) ? f.Value : toggles.TryGetValue(p, out ToggleOption? t) && t.Checked ? "1" : "";

        private void ResetForm()
        {
            foreach (var (p, f) in fields) { f.Value = p.Default; f.Error = null; }
            foreach (ToggleOption t in toggles.Values) { t.Checked = false; }
            banner.Hide2();
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            // Preview không kiểm tra tồn tại file để tránh nhấp nháy lỗi khi đang gõ; chỉ cần đủ tham số bắt buộc
            BuildResult r = CommandLine.Build(command, ValueOf);
            bool complete = r.IsValid;
            if (complete)
            {
                preview.Show(CommandLine.Format(command.Name, r.Args), true);
            }
            else
            {
                string missing = string.Join(", ", r.Errors.Keys.Select(p => p.Label));
                preview.Show("(Chưa đủ thông tin: " + missing + ")", false);
            }
            copyButton.Enabled = complete;
        }

        private void CopyCommand()
        {
            if (!preview.IsValid) { return; }
            try
            {
                Clipboard.SetText(preview.Command);
                banner.Show(LogLevel.Info, "Đã chép dòng lệnh (không chứa mật khẩu) vào clipboard.");
                PerformLayout();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                banner.Show(LogLevel.Warning, "Không truy cập được clipboard, thử lại sau.");
                PerformLayout();
            }
        }

        private void UpdateBusy()
        {
            bool busy = runner.IsBusy;
            runButton.Enabled = !busy;
            refreshButton.Enabled = !busy;
            foreach (FormField f in fields.Values) { f.Enabled = !busy; }
            foreach (ToggleOption t in toggles.Values) { t.Enabled = !busy; }
            if (!busy) { runButton.Loading = false; }
        }

        private async Task ExecuteAsync()
        {
            BuildResult r = CommandLine.Build(command, ValueOf);
            foreach (var (p, f) in fields) { f.Error = r.Errors.GetValueOrDefault(p); }
            if (!r.IsValid)
            {
                banner.Show(LogLevel.Warning, "Vui lòng kiểm tra các trường được đánh dấu đỏ.");
                PerformLayout();
                fields.First(kv => r.Errors.ContainsKey(kv.Key)).Value.Focus();
                return;
            }

            bool applying = applyParam != null && toggles[applyParam].Checked;
            if (applying && !ConfirmationDialog.Ask(FindForm()!, "Xác nhận ghi dữ liệu thật",
                    $"Lệnh \"{command.Title}\" sẽ GHI vào database. Hành động này có thể không hoàn tác được.", "Ghi thật"))
            {
                return;
            }

            banner.Hide2();
            SaveValues();
            ParamDef? passwordParam = command.Params.FirstOrDefault(p => p.Kind == ParamKind.Password);
            string? password = passwordParam == null ? null : fields[passwordParam].Value;

            log.Write(LogLevel.Info, "▶ " + CommandLine.Format(command.Name, r.Args));
            log.Write(LogLevel.Info, applying ? "Chế độ: GHI THẬT" : "Chế độ: kiểm thử (không ghi database)");
            if (ReturnsPrivateKey(applying))
            {
                log.Write(LogLevel.Warning, "private_key chỉ hiển thị một lần trong nhật ký này và không được lưu ra file — hãy sao chép ngay.");
            }

            runButton.Loading = true;
            RunResult result = await runner.RunAsync(command, [.. r.Args], password, log);
            UpdateBusy();

            if (!result.Completed)
            {
                log.Write(LogLevel.Error, "Lệnh không chạy hoàn tất.");
                banner.Show(LogLevel.Error, "Lệnh không chạy hoàn tất. Xem nhật ký thực thi.");
            }
            else if (result.ExitCode != 0)
            {
                log.Write(LogLevel.Error, $"Thất bại, mã thoát {result.ExitCode}.");
                banner.Show(LogLevel.Error, $"Thực hiện thất bại (mã thoát {result.ExitCode}). Xem nhật ký thực thi.");
            }
            else if (applying || applyParam == null)
            {
                log.Write(LogLevel.Success, "Hoàn tất, mã thoát 0.");
                banner.Show(LogLevel.Success, "Thực hiện thành công.");
            }
            else
            {
                log.Write(LogLevel.Success, "Kiểm thử hoàn tất, chưa ghi database.");
                banner.Show(LogLevel.Info, "Kiểm thử hoàn tất — chưa ghi dữ liệu. Chọn \"Áp dụng (ghi thật)\" để thực hiện.");
            }
            PerformLayout();
        }

        private bool ReturnsPrivateKey(bool applying) =>
            applying && (command.Name == "issue-key" || command.Params.Any(p => p.ArgName == "--issue-key" && ValueOf(p).Length > 0));

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!ready) { return; }
            int width = Math.Max(480, ClientSize.Width - Pad * 2);
            int cols = width >= 760 ? 2 : 1;
            int y = Pad;

            header.SetBounds(Pad, y, width, PageHeader.HeaderHeight);
            y += header.Height + Theme.Gap;

            if (banner.Visible)
            {
                banner.SetBounds(Pad, y, width, NotificationBanner.BannerHeight);
                y += banner.Height + Theme.Gap;
            }

            y = PlaceGrid(formCard, fields.Values.Cast<Control>().ToList(), FormField.FieldHeight, cols, width, y);
            if (optionsCard.Visible) { y = PlaceGrid(optionsCard, toggles.Values.Cast<Control>().ToList(), 78, cols, width, y); }

            previewCard.SetBounds(Pad, y, width, Card.ContentTop + preview.Height + 8);
            preview.SetBounds(Card.Inset, 62, width - Card.Inset * 2, preview.Height);
            y += previewCard.Height + Theme.Gap;

            runButton.Width = Math.Max(runButton.Width, 150);
            int x = Pad + width - runButton.Width;
            runButton.Location = new Point(x, y);
            x -= logButton.Width + 12; logButton.Location = new Point(x, y);
            x -= copyButton.Width + 12; copyButton.Location = new Point(x, y);
            refreshButton.Location = new Point(Pad, y);

            AutoScrollMinSize = new Size(0, y + runButton.Height + Pad);
        }

        /// <summary>Xếp các control con thành lưới <paramref name="cols"/> cột trong card, trả về y kế tiếp.</summary>
        private static int PlaceGrid(Card card, List<Control> children, int cellHeight, int cols, int width, int y)
        {
            int inner = width - Card.Inset * 2;
            int cellWidth = (inner - (cols - 1) * Theme.Gap * 2) / cols;
            for (int i = 0; i < children.Count; i++)
            {
                int row = i / cols, col = i % cols;
                children[i].SetBounds(Card.Inset + col * (cellWidth + Theme.Gap * 2), 62 + row * (cellHeight + 4), cellWidth, cellHeight);
            }
            int rows = (children.Count + cols - 1) / cols;
            int height = 62 + rows * (cellHeight + 4) + 12;
            card.SetBounds(Pad, y, width, height);
            return y + height + Theme.Gap;
        }
    }
}
