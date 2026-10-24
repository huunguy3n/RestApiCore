namespace PortalApp.Ui
{
    /// <summary>Đầu trang: ô icon, tiêu đề, mã chức năng nhỏ bên cạnh và mô tả ngắn.</summary>
    public sealed class PageHeader : Control
    {
        public const int HeaderHeight = 104;

        private string title = "", code = "", description = "", icon = "";

        public PageHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Height = HeaderHeight;
        }

        public void Set(string icon, string title, string code, string description)
        {
            this.icon = icon;
            this.title = title;
            this.code = code;
            this.description = description;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            Theme.FillRound(g, ClientRectangle, Theme.Surface, Theme.Border, 10);

            var tile = new Rectangle(24, 22, 60, 60);
            Theme.FillRound(g, tile, Theme.PrimarySoft, null, 12);
            using Font iconFont = Theme.Icons(24f);
            TextRenderer.DrawText(g, icon, iconFont, tile, Theme.Primary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            using Font titleFont = Theme.Body(18f, FontStyle.Bold);
            TextRenderer.DrawText(g, title, titleFont, new Point(104, 18), Theme.TextPrimary);
            if (code.Length > 0)
            {
                int w = TextRenderer.MeasureText(title, titleFont).Width;
                var tag = new Rectangle(104 + w + 4, 26, TextRenderer.MeasureText(code, Theme.Mono(9f)).Width + 16, 24);
                Theme.FillRound(g, tag, Theme.SurfaceAlt, Theme.Border, 6);
                TextRenderer.DrawText(g, code, Theme.Mono(9f), tag, Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            TextRenderer.DrawText(g, description, Theme.Body(10f), new Rectangle(104, 56, Width - 128, 40), Theme.TextSecondary,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }
}
