using System.ComponentModel;

namespace Gui.Ui
{
    public enum ButtonStyle { Primary, Secondary, Danger }

    /// <summary>Nút ba cấp độ (Primary / Secondary / Danger) vẽ tay, có icon, hover, focus và trạng thái loading.</summary>
    public sealed class AppButton : Control
    {
        private bool hover, pressed, loading;
        private string baseText = "";

        public AppButton(string text, string icon, ButtonStyle style)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = Theme.Body(9.5f, FontStyle.Bold);
            Height = Theme.InputHeight;
            Icon = icon;
            Style = style;
            baseText = text;
            base.Text = text;
            Width = MeasureWidth();
        }

        [Browsable(false)] public string Icon { get; }
        [Browsable(false)] public ButtonStyle Style { get; }

        /// <summary>Trong lúc loading nút bị khóa và hiện nhãn khác để không bấm lặp.</summary>
        public bool Loading
        {
            get => loading;
            set { loading = value; base.Text = value ? "Đang xử lý…" : baseText; Invalidate(); }
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => base.Text;
            set { baseText = value ?? ""; base.Text = baseText; Width = MeasureWidth(); Invalidate(); }
        }

        private int MeasureWidth() => TextRenderer.MeasureText(baseText, Font).Width + 64;

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled && !loading) { base.OnClick(e); }
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            bool off = !Enabled || loading;

            (Color fill, Color border, Color text) = Style switch
            {
                ButtonStyle.Primary => (pressed || hover ? Theme.PrimaryHover : Theme.Primary, Theme.Primary, Color.White),
                ButtonStyle.Danger => (pressed || hover ? Color.FromArgb(185, 28, 28) : Theme.Error, Theme.Error, Color.White),
                _ => (hover ? Theme.PrimarySoft : Theme.Surface, Theme.Border, Theme.TextPrimary)
            };
            if (!Enabled)
            {
                fill = Style == ButtonStyle.Secondary ? Theme.SurfaceAlt : Color.FromArgb(160, 174, 196);
                border = Theme.Border;
                text = Style == ButtonStyle.Secondary ? Color.FromArgb(160, 170, 185) : Color.White;
            }
            else if (loading) { fill = Style == ButtonStyle.Secondary ? Theme.SurfaceAlt : Theme.PrimaryHover; }

            Theme.FillRound(g, ClientRectangle, fill, border);

            string icon = loading ? Theme.Icon.Refresh : Icon;
            using Font iconFont = Theme.Icons(11f);
            Size iconSize = TextRenderer.MeasureText(icon, iconFont);
            Size textSize = TextRenderer.MeasureText(base.Text, Font);
            int total = iconSize.Width + 8 + textSize.Width;
            int x = (Width - total) / 2;
            TextRenderer.DrawText(g, icon, iconFont, new Point(x, (Height - iconSize.Height) / 2), text);
            TextRenderer.DrawText(g, base.Text, Font, new Point(x + iconSize.Width + 8, (Height - textSize.Height) / 2), text);

            if (Focused && ShowFocusCues && !off)
            {
                using var pen = new Pen(Theme.Primary, 2);
                using var path = Theme.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), Theme.Radius);
                g.DrawPath(pen, path);
            }
        }
    }
}
