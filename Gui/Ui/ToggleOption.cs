namespace Gui.Ui
{
    /// <summary>
    /// Tuỳ chọn bật/tắt dạng thẻ: icon, tiêu đề, mô tả và checkbox. Kiểu <see cref="OptionTone.Risk"/>
    /// đổi màu cam khi bật để phân biệt rõ với thao tác chỉ kiểm thử.
    /// </summary>
    public enum OptionTone { Normal, Risk }

    public sealed class ToggleOption : Control
    {
        private readonly string icon, title, description;
        private readonly OptionTone tone;
        private bool hover;

        public ToggleOption(string icon, string title, string description, OptionTone tone)
        {
            this.icon = icon;
            this.title = title;
            this.description = description;
            this.tone = tone;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Height = 78;
        }

        private bool isChecked;

        public bool Checked
        {
            get => isChecked;
            set { if (isChecked == value) { return; } isChecked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }

        public event EventHandler? CheckedChanged;

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !Checked; }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            bool risk = tone == OptionTone.Risk && isChecked;
            Color fill = risk ? Theme.WarningSoft : isChecked ? Theme.PrimarySoft : hover ? Theme.PrimarySoft : Theme.SurfaceAlt;
            Color border = risk ? Theme.Warning : Focused ? Theme.Primary : Theme.Border;
            Theme.FillRound(g, ClientRectangle, fill, border);

            Color accent = risk ? Theme.Warning : Theme.Primary;
            using Font iconFont = Theme.Icons(15f);
            TextRenderer.DrawText(g, icon, iconFont, new Point(16, 26), accent);

            var boxRect = new Rectangle(52, 28, 22, 22);
            Theme.FillRound(g, boxRect, isChecked ? accent : Theme.Surface, isChecked ? accent : Color.FromArgb(160, 172, 190), 5);
            if (isChecked)
            {
                using Font tick = Theme.Icons(10f);
                TextRenderer.DrawText(g, Theme.Icon.Check, tick, boxRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            TextRenderer.DrawText(g, title, Theme.Body(10f, FontStyle.Bold), new Point(88, 14), Theme.TextPrimary);
            var descRect = new Rectangle(88, 38, Width - 100, Height - 42);
            TextRenderer.DrawText(g, description, Theme.Body(9f), descRect, Theme.TextSecondary,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }
}
