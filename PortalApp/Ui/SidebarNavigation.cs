namespace PortalApp.Ui
{
    public sealed record NavEntry(string Key, string Title, string Icon);

    /// <summary>Thanh điều hướng trái: logo, danh sách mục có hover/focus/selected, phiên bản ở cuối.</summary>
    public sealed class SidebarNavigation : Panel
    {
        public const int SidebarWidth = 232;

        private readonly List<NavItem> items = [];
        private readonly string version;

        public SidebarNavigation(IEnumerable<NavEntry> entries, string appName, string version)
        {
            this.version = version;
            Width = SidebarWidth;
            Dock = DockStyle.Left;
            BackColor = Theme.Sidebar;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            int y = 84;
            foreach (NavEntry entry in entries)
            {
                var item = new NavItem(entry) { Left = 12, Top = y, Width = SidebarWidth - 24, Height = 46 };
                item.Click += (_, _) => Select(entry.Key);
                items.Add(item);
                Controls.Add(item);
                y += 52;
            }
            AppName = appName;
        }

        public string AppName { get; }
        public string? SelectedKey { get; private set; }
        public event EventHandler<string>? Navigated;

        public void Select(string key)
        {
            SelectedKey = key;
            foreach (NavItem item in items) { item.Selected = item.Entry.Key == key; }
            Navigated?.Invoke(this, key);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.FillRound(g, new Rectangle(16, 22, 38, 38), Theme.Primary, null, 10);
            using Font iconFont = Theme.Icons(16f);
            TextRenderer.DrawText(g, Theme.Icon.Database, iconFont, new Rectangle(16, 22, 38, 38), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, AppName, Theme.Body(12f, FontStyle.Bold), new Point(64, 29), Color.White);

            string footer = "v" + version;
            TextRenderer.DrawText(g, footer, Theme.Body(9f), new Point(20, Height - 34), Color.FromArgb(150, 170, 205));
        }

        private sealed class NavItem : Control
        {
            private bool hover;

            public NavItem(NavEntry entry)
            {
                Entry = entry;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
                TabStop = true;
                Cursor = Cursors.Hand;
            }

            public NavEntry Entry { get; }
            private bool selected;
            public bool Selected { get => selected; set { selected = value; Invalidate(); } }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
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
                g.Clear(Theme.Sidebar);
                if (selected) { Theme.FillRound(g, ClientRectangle, Theme.Primary); }
                else if (hover) { Theme.FillRound(g, ClientRectangle, Theme.SidebarHover); }
                if (Focused && !selected)
                {
                    using var pen = new Pen(Color.FromArgb(120, 160, 230), 1);
                    using var path = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Radius);
                    g.DrawPath(pen, path);
                }

                Color fg = selected ? Color.White : Theme.SidebarText;
                using Font iconFont = Theme.Icons(14f);
                TextRenderer.DrawText(g, Entry.Icon, iconFont, new Rectangle(8, 0, 40, Height), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, Entry.Title, Theme.Body(10.5f, selected ? FontStyle.Bold : FontStyle.Regular),
                    new Rectangle(54, 0, Width - 60, Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
