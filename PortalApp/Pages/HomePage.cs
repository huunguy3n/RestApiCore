using PortalApp.Ui;

namespace PortalApp.Pages
{
    /// <summary>Trang chủ: lối tắt tới từng lệnh (lấy từ danh mục thật) và nhắc nguyên tắc kiểm thử trước khi ghi.</summary>
    public sealed class HomePage : UserControl
    {
        private const int Pad = 24, TileHeight = 120;

        private readonly PageHeader header = new();
        private readonly List<Tile> tiles = [];

        public HomePage(IEnumerable<(CommandDef Command, string Icon)> commands, Action<string> open)
        {
            BackColor = Theme.Background;
            AutoScroll = true;
            DoubleBuffered = true;
            header.Set(Theme.Icon.Home, "Trang chủ", "", "Công cụ quản trị Fast.Api. Mọi lệnh mặc định chạy kiểm thử; chỉ ghi database khi bạn chọn \"Áp dụng (ghi thật)\" và xác nhận.");
            Controls.Add(header);
            foreach (var (command, icon) in commands)
            {
                var tile = new Tile(icon, command.Title, command.Description);
                tile.Click += (_, _) => open(command.Name);
                tiles.Add(tile);
                Controls.Add(tile);
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int width = Math.Max(480, ClientSize.Width - Pad * 2);
            header.SetBounds(Pad, Pad, width, PageHeader.HeaderHeight);
            int cols = width >= 900 ? 3 : width >= 600 ? 2 : 1;
            int cell = (width - (cols - 1) * Theme.Gap) / cols;
            int y = Pad + PageHeader.HeaderHeight + Theme.Gap;
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].SetBounds(Pad + (i % cols) * (cell + Theme.Gap), y + (i / cols) * (TileHeight + Theme.Gap), cell, TileHeight);
            }
            int rows = (tiles.Count + cols - 1) / cols;
            AutoScrollMinSize = new Size(0, y + rows * (TileHeight + Theme.Gap) + Pad);
        }

        private sealed class Tile : Control
        {
            private readonly string icon, title, description;
            private bool hover;

            public Tile(string icon, string title, string description)
            {
                this.icon = icon;
                this.title = title;
                this.description = description;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
                TabStop = true;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

            protected override void OnKeyUp(KeyEventArgs e)
            {
                if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); }
                base.OnKeyUp(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Background);
                Theme.FillRound(g, ClientRectangle, hover ? Theme.PrimarySoft : Theme.Surface, Focused || hover ? Theme.Primary : Theme.Border, 10);
                using Font iconFont = Theme.Icons(18f);
                TextRenderer.DrawText(g, icon, iconFont, new Point(20, 20), Theme.Primary);
                TextRenderer.DrawText(g, title, Theme.Body(11.5f, FontStyle.Bold), new Point(56, 20), Theme.TextPrimary);
                TextRenderer.DrawText(g, description, Theme.Body(9.5f), new Rectangle(20, 56, Width - 40, Height - 62), Theme.TextSecondary,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
