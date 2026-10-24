namespace Gui.Ui
{
    /// <summary>
    /// Card trắng bo góc, viền nhẹ, có tiêu đề nhóm (icon + chữ). Bố cục bên trong do trang đặt theo toạ độ
    /// bắt đầu từ <see cref="ContentTop"/>; card chỉ lo phần khung.
    /// </summary>
    public sealed class Card : Panel
    {
        public const int ContentTop = 56;
        public const int Inset = 24;

        private readonly string icon;
        private readonly string title;

        public Card(string icon, string title)
        {
            this.icon = icon;
            this.title = title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            Theme.FillRound(g, ClientRectangle, Theme.Surface, Theme.Border, 10);
            if (title.Length == 0) { return; }

            using Font iconFont = Theme.Icons(13f);
            using Font titleFont = Theme.Body(11f, FontStyle.Bold);
            TextRenderer.DrawText(g, icon, iconFont, new Point(Inset, 17), Theme.Primary);
            TextRenderer.DrawText(g, title, titleFont, new Point(Inset + 30, 15), Theme.TextPrimary);
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, Inset, 46, Width - Inset, 46);
        }
    }
}
