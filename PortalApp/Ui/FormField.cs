namespace PortalApp.Ui
{
    /// <summary>
    /// Một trường nhập: nhãn phía trên, ô nhập có icon, nút phụ (chọn file / thư mục / hiện mật khẩu) và dòng lỗi.
    /// Chiều cao cố định (đã chừa chỗ cho lỗi) để bố cục không nhảy khi hiện lỗi.
    /// </summary>
    public sealed class FormField : Control
    {
        public const int FieldHeight = 92;

        private readonly TextBox input = new() { BorderStyle = BorderStyle.None, Font = Theme.Body(10f), BackColor = Theme.Surface };
        private readonly List<(string Icon, string Tip, Rectangle Bounds, Action Click)> actions = [];
        private readonly ToolTip tip = new();
        private readonly string label, leadIcon, placeholder;
        private readonly bool required, password;
        private string? error;
        private bool revealed;
        private bool boxFocused;
        private Rectangle box;

        public FormField(string label, string leadIcon, string placeholder, bool required, bool password, string help)
        {
            this.label = label;
            this.leadIcon = leadIcon;
            this.placeholder = placeholder;
            this.required = required;
            this.password = password;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Height = FieldHeight;
            input.PlaceholderText = placeholder;
            input.UseSystemPasswordChar = password;
            input.TextChanged += (_, _) => { if (error != null) { Error = null; } Invalidate(); TextChangedByUser?.Invoke(this, EventArgs.Empty); };
            input.GotFocus += (_, _) => { boxFocused = true; Invalidate(); };
            input.LostFocus += (_, _) => { boxFocused = false; Invalidate(); };
            Controls.Add(input);
            if (help.Length > 0) { tip.SetToolTip(input, help); }

            if (password)
            {
                AddAction(Theme.Icon.Eye, "Hiện / ẩn mật khẩu", () =>
                {
                    revealed = !revealed;
                    input.UseSystemPasswordChar = !revealed;
                    Invalidate();
                });
            }
        }

        public event EventHandler? TextChangedByUser;

        public string Value
        {
            get => input.Text.Trim();
            set => input.Text = value;
        }

        public string? Error
        {
            get => error;
            set { error = value; Invalidate(); }
        }

        public void AddAction(string icon, string tooltip, Action click)
        {
            actions.Add((icon, tooltip, Rectangle.Empty, click));
            PerformLayoutBoxes();
        }

        public void AddTextAction(string text, Action click) => AddAction("T:" + text, text, click);

        protected override void OnResize(EventArgs e) { base.OnResize(e); PerformLayoutBoxes(); }

        private void PerformLayoutBoxes()
        {
            box = new Rectangle(0, 26, Width, Theme.InputHeight);
            int right = box.Right - 6;
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var a = actions[i];
                int w = a.Icon.StartsWith("T:") ? TextRenderer.MeasureText(a.Icon[2..], Theme.Body(9.5f, FontStyle.Bold)).Width + 40 : 34;
                a.Bounds = new Rectangle(right - w, box.Y + 4, w, box.Height - 8);
                actions[i] = a;
                right = a.Bounds.X - 4;
            }
            int left = box.X + 40;
            input.SetBounds(left, box.Y + (box.Height - input.PreferredHeight) / 2, Math.Max(20, right - left), input.PreferredHeight);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = actions.Any(a => a.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            foreach (var a in actions.ToList())
            {
                if (a.Bounds.Contains(e.Location)) { a.Click(); return; }
            }
            if (box.Contains(e.Location)) { input.Focus(); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            using Font labelFont = Theme.Body(10f, FontStyle.Bold);
            TextRenderer.DrawText(g, label, labelFont, new Point(0, 0), Theme.TextPrimary);
            if (required)
            {
                int w = TextRenderer.MeasureText(label, labelFont).Width - 4;
                TextRenderer.DrawText(g, "*", labelFont, new Point(w, 0), Theme.Error);
            }

            Color border = error != null ? Theme.Error : boxFocused ? Theme.Primary : Theme.Border;
            Theme.FillRound(g, box, Theme.Surface, border);
            if (boxFocused || error != null)
            {
                using var pen = new Pen(border, 2);
                using var path = Theme.RoundRect(new Rectangle(box.X + 1, box.Y + 1, box.Width - 3, box.Height - 3), Theme.Radius);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawPath(pen, path);
            }

            using Font iconFont = Theme.Icons(12f);
            TextRenderer.DrawText(g, leadIcon, iconFont, new Point(box.X + 12, box.Y + 11), Theme.TextSecondary);

            foreach (var a in actions)
            {
                if (a.Icon.StartsWith("T:"))
                {
                    Theme.FillRound(g, a.Bounds, Theme.PrimarySoft, null, 6);
                    TextRenderer.DrawText(g, a.Icon[2..], Theme.Body(9.5f, FontStyle.Bold), a.Bounds, Theme.Primary,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    string glyph = password && a.Icon == Theme.Icon.Eye && revealed ? Theme.Icon.EyeOff : a.Icon;
                    TextRenderer.DrawText(g, glyph, iconFont, a.Bounds, Theme.TextSecondary,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }

            if (error != null)
            {
                TextRenderer.DrawText(g, error, Theme.Body(9f), new Point(0, box.Bottom + 6), Theme.Error);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
