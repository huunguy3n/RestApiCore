namespace PortalApp.Ui
{
    public enum LogLevel { Info, Success, Warning, Error }

    /// <summary>Đích ghi log; Runner chỉ biết giao diện này, không biết WinForms.</summary>
    public interface ILogSink
    {
        void Write(LogLevel level, string message);
    }

    /// <summary>Nhật ký thực thi kiểu terminal: giờ, mức log, nội dung. Thu gọn/mở rộng được, xóa được. An toàn khi gọi từ thread khác.</summary>
    public sealed class ExecutionLogPanel : Panel, ILogSink
    {
        public const int HeaderHeight = 44;
        public const int ExpandedHeight = 150;

        private readonly RichTextBox log = new()
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.LogBackground, ForeColor = Theme.LogText,
            Font = Theme.Mono(9.5f), DetectUrls = false, WordWrap = true
        };
        private readonly Panel header = new() { Height = HeaderHeight, Dock = DockStyle.Top, BackColor = Theme.LogHeader };
        private readonly Label title = new() { Text = "Nhật ký thực thi", ForeColor = Color.White, Font = Theme.Body(10.5f, FontStyle.Bold), AutoSize = true, Left = 44, Top = 12 };
        private readonly Label icon = new() { Text = Theme.Icon.Terminal, ForeColor = Color.White, Font = Theme.Icons(13f), AutoSize = true, Left = 16, Top = 13 };
        private readonly Label clear = HeaderLink("Xóa log");
        private readonly Label toggle = HeaderLink(Theme.Icon.Down);
        private bool expanded = true;

        public ExecutionLogPanel()
        {
            Height = ExpandedHeight;
            BackColor = Theme.LogBackground;
            Padding = new Padding(0);

            log.Dock = DockStyle.Fill;
            header.Controls.AddRange([icon, title, clear, toggle]);
            Controls.Add(log);
            Controls.Add(header);

            clear.Click += (_, _) => Clear();
            toggle.Click += (_, _) => Expanded = !Expanded;
            header.Click += (_, _) => Expanded = !Expanded;
            header.Resize += (_, _) => PlaceLinks();
            PlaceLinks();
        }

        public bool Expanded
        {
            get => expanded;
            set
            {
                expanded = value;
                toggle.Text = value ? Theme.Icon.Down : Theme.Icon.Up;
                Height = value ? ExpandedHeight : HeaderHeight;
            }
        }

        public void Clear() => log.Clear();

        public void Write(LogLevel level, string message)
        {
            if (InvokeRequired) { BeginInvoke(() => Write(level, message)); return; }
            // Phải mở rộng để người dùng thấy lỗi ngay, kể cả khi đang thu gọn
            if (!expanded && level is LogLevel.Error or LogLevel.Warning) { Expanded = true; }

            foreach (string line in message.Replace("\r", "").Split('\n'))
            {
                if (line.Length == 0) { continue; }
                (string tag, Color color) = level switch
                {
                    LogLevel.Success => ("OK  ", Color.FromArgb(74, 222, 128)),
                    LogLevel.Warning => ("WARN", Color.FromArgb(251, 191, 36)),
                    LogLevel.Error => ("ERR ", Color.FromArgb(248, 113, 113)),
                    _ => ("INFO", Color.FromArgb(96, 165, 250))
                };
                Append($"[{DateTime.Now:HH:mm:ss}] ", Color.FromArgb(130, 144, 168));
                Append(tag + " ", color);
                Append(line + "\n", level == LogLevel.Error ? Color.FromArgb(252, 165, 165) : Theme.LogText);
            }
            log.ScrollToCaret();
        }

        private void Append(string text, Color color)
        {
            log.SelectionStart = log.TextLength;
            log.SelectionColor = color;
            log.AppendText(text);
        }

        private void PlaceLinks()
        {
            toggle.Left = header.Width - toggle.Width - 14;
            clear.Left = toggle.Left - clear.Width - 6;
        }

        private static Label HeaderLink(string text) => new()
        {
            Text = text, AutoSize = true, ForeColor = Color.FromArgb(200, 210, 228), Cursor = Cursors.Hand, Top = 12,
            Font = text.Length == 1 ? Theme.Icons(11f) : Theme.Body(9.5f), Padding = new Padding(6, 2, 6, 2)
        };
    }
}
