using PortalApp.Core;

namespace PortalApp.Ui
{
    /// <summary>
    /// Khung trang của PortalApp: đầu trang + các mục xếp dọc (nhãn trái, ô nhập phải), nút có trạng thái đang xử lý.
    /// Mọi lỗi chỉ ghi Message vào nhật ký (không stack trace, không dữ liệu nhập, không connection string)
    /// </summary>
    public abstract class PageBase : UserControl
    {
        private const int Pad = 24;
        private readonly PageHeader header = new();
        private readonly FlowLayoutPanel body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Background };
        private readonly List<Control> sections = [];

        protected PageBase(LocalProject project, ILogSink log, string icon, string title, string description)
        {
            Project = project;
            Log = log;
            BackColor = Theme.Background;
            DoubleBuffered = true;
            header.Set(icon, title, project.ProjectCode, description);
            Controls.Add(body);
            Controls.Add(header);
        }

        protected LocalProject Project { get; }
        protected ILogSink Log { get; }

        /// <summary>Gọi mỗi lần trang được mở (làm mới dữ liệu)</summary>
        public virtual Task RefreshAsync() => Task.CompletedTask;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            header.SetBounds(Pad, Pad, Math.Max(400, ClientSize.Width - Pad * 2), PageHeader.HeaderHeight);
            body.SetBounds(Pad, Pad + PageHeader.HeaderHeight + Theme.Gap, Math.Max(400, ClientSize.Width - Pad * 2),
                Math.Max(100, ClientSize.Height - PageHeader.HeaderHeight - Pad * 2 - Theme.Gap));
            foreach (Control section in sections)
            {
                section.Width = body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
            }
        }

        /// <summary>Một mục có tiêu đề; trả lưới 2 cột (nhãn, ô nhập) để thêm dòng</summary>
        protected TableLayoutPanel AddSection(string title, string? note = null)
        {
            Panel panel = new() { BackColor = Theme.Surface, Padding = new Padding(20, 12, 20, 16), Margin = new Padding(0, 0, 0, Theme.Gap), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            TableLayoutPanel grid = new() { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Surface };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label heading = new() { Text = title, Font = Theme.Body(11.5f, FontStyle.Bold), ForeColor = Theme.TextPrimary, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            grid.Controls.Add(heading, 0, 0);
            grid.SetColumnSpan(heading, 2);
            if (note != null)
            {
                Label hint = new() { Text = note, Font = Theme.Body(9.5f), ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(0, 0, 0, 8) };
                grid.Controls.Add(hint, 0, 1);
                grid.SetColumnSpan(hint, 2);
            }
            panel.Controls.Add(grid);
            sections.Add(panel);
            body.Controls.Add(panel);
            PerformLayout();
            return grid;
        }

        protected static void AddRow(TableLayoutPanel grid, string label, Control control)
        {
            int row = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Font = Theme.Body(), ForeColor = Theme.TextPrimary, Margin = new Padding(0, 8, 8, 4) }, 0, row);
            control.Margin = new Padding(0, 4, 0, 4);
            if (control is TextBox or ComboBox or DataGridView)
            {
                control.Dock = DockStyle.Fill;
            }
            grid.Controls.Add(control, 1, row);
        }

        protected static void AddWide(TableLayoutPanel grid, Control control)
        {
            int row = grid.RowCount++;
            control.Margin = new Padding(0, 6, 0, 4);
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
        }

        protected static TextBox Input(string text = "", bool password = false, bool multiline = false, int height = 0) => new()
        {
            Text = text,
            Font = multiline ? Theme.Mono(9.5f) : Theme.Body(10f),
            UseSystemPasswordChar = password,
            Multiline = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            Height = multiline ? Math.Max(height, 80) : 28,
            MinimumSize = new Size(300, 0)
        };

        protected static Label Info(string text = "") => new() { Text = text, AutoSize = true, MaximumSize = new Size(900, 0), Font = Theme.Body(), ForeColor = Theme.TextSecondary };

        protected static FlowLayoutPanel Buttons(params Control[] buttons)
        {
            FlowLayoutPanel flow = new() { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Surface };
            foreach (Control button in buttons)
            {
                button.Margin = new Padding(0, 0, 8, 0);
                flow.Controls.Add(button);
            }
            return flow;
        }

        protected static DataGridView Grid(int height = 260) => new()
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            BackgroundColor = Theme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Height = height,
            Font = Theme.Body(9.5f)
        };

        /// <summary>Chạy thao tác: khóa nút, bắt lỗi, ghi nhật ký</summary>
        protected async Task RunAsync(AppButton button, Func<Task> action)
        {
            if (button.Loading)
            {
                return;
            }
            button.Loading = true;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Write(LogLevel.Error, ex is Microsoft.Data.SqlClient.SqlException sql ? $"Lỗi SQL {sql.Number}: {sql.Message}" : ex.Message);
            }
            finally
            {
                button.Loading = false;
            }
        }

        protected bool Confirm(string title, string message, string confirmText) => ConfirmationDialog.Ask(this, title, message, confirmText);
    }
}
