namespace PortalApp.Ui
{
    /// <summary>Thông báo ngay trong trang (thành công / cảnh báo / lỗi). Không tự biến mất để người dùng kịp đọc; có nút đóng.</summary>
    public sealed class NotificationBanner : Control
    {
        public const int BannerHeight = 52;

        private LogLevel level;
        private string message = "";
        private Rectangle closeRect;

        public NotificationBanner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Height = BannerHeight;
            Visible = false;
        }

        public event EventHandler? Dismissed;

        public void Show(LogLevel level, string message)
        {
            this.level = level;
            this.message = message;
            Visible = true;
            Invalidate();
        }

        public void Hide2()
        {
            if (!Visible) { return; }
            Visible = false;
            Dismissed?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (closeRect.Contains(e.Location)) { Hide2(); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            (Color fill, Color accent, string icon) = level switch
            {
                LogLevel.Success => (Theme.SuccessSoft, Theme.Success, Theme.Icon.Check),
                LogLevel.Warning => (Theme.WarningSoft, Theme.Warning, Theme.Icon.Warn),
                LogLevel.Error => (Theme.ErrorSoft, Theme.Error, Theme.Icon.Error),
                _ => (Theme.PrimarySoft, Theme.Primary, Theme.Icon.Info)
            };
            Theme.FillRound(g, ClientRectangle, fill, accent);
            using Font iconFont = Theme.Icons(14f);
            TextRenderer.DrawText(g, icon, iconFont, new Point(16, 16), accent);
            TextRenderer.DrawText(g, message, Theme.Body(10f), new Rectangle(46, 0, Width - 100, Height), Theme.TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            closeRect = new Rectangle(Width - 44, 8, 36, 36);
            using Font small = Theme.Icons(10f);
            TextRenderer.DrawText(g, Theme.Icon.Close, small, closeRect, Theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
