using System.Drawing.Drawing2D;

namespace PortalApp.Ui
{
    /// <summary>Design tokens dùng chung cho toàn bộ giao diện: màu, font, kích thước, icon.</summary>
    public static class Theme
    {
        public static readonly Color Primary = Color.FromArgb(37, 99, 235);
        public static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
        public static readonly Color PrimarySoft = Color.FromArgb(219, 234, 254);
        public static readonly Color Sidebar = Color.FromArgb(15, 40, 84);
        public static readonly Color SidebarHover = Color.FromArgb(30, 58, 110);
        public static readonly Color SidebarText = Color.FromArgb(214, 224, 241);
        public static readonly Color Background = Color.FromArgb(241, 245, 250);
        public static readonly Color Surface = Color.White;
        public static readonly Color SurfaceAlt = Color.FromArgb(247, 249, 252);
        public static readonly Color Border = Color.FromArgb(218, 225, 235);
        public static readonly Color TextPrimary = Color.FromArgb(22, 34, 56);
        public static readonly Color TextSecondary = Color.FromArgb(100, 112, 130);
        public static readonly Color Success = Color.FromArgb(22, 163, 74);
        public static readonly Color Warning = Color.FromArgb(217, 119, 6);
        public static readonly Color WarningSoft = Color.FromArgb(254, 243, 226);
        public static readonly Color Error = Color.FromArgb(220, 38, 38);
        public static readonly Color ErrorSoft = Color.FromArgb(254, 235, 235);
        public static readonly Color SuccessSoft = Color.FromArgb(228, 247, 235);
        public static readonly Color LogBackground = Color.FromArgb(17, 24, 39);
        public static readonly Color LogHeader = Color.FromArgb(24, 33, 52);
        public static readonly Color LogText = Color.FromArgb(209, 218, 232);

        public const int Radius = 8;
        public const int Gap = 16;
        public const int InputHeight = 40;

        public static Font Body(float size = 9.5f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
        public static Font Mono(float size = 9.5f) => new("Consolas", size);
        public static Font Icons(float size = 12f) => new("Segoe MDL2 Assets", size);

        // Glyph của Segoe MDL2 Assets (có sẵn trên Windows 10/11, không cần thêm gói icon)
        public static class Icon
        {
            public const string Home = "";
            public const string AddUser = "";
            public const string User = "";
            public const string Lock = "";
            public const string Key = "";
            public const string Rekey = "";
            public const string Upload = "";
            public const string File = "";
            public const string Folder = "";
            public const string Copy = "";
            public const string Refresh = "";
            public const string Play = "";
            public const string Delete = "";
            public const string Eye = "";
            public const string EyeOff = "";
            public const string Up = "";
            public const string Down = "";
            public const string Close = "";
            public const string Check = "";
            public const string Error = "";
            public const string Warn = "";
            public const string Info = "";
            public const string Terminal = "";
            public const string Doc = "";
            public const string Database = "";
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Rectangle r, Color fill, Color? border = null, int radius = Radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = RoundRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            if (border != null)
            {
                using var pen = new Pen(border.Value);
                g.DrawPath(pen, path);
            }
        }
    }
}
