namespace Gui.Ui
{
    /// <summary>Khung xem dòng lệnh CLI tương đương (monospace). Sao chép chỉ khả dụng khi lệnh hợp lệ.</summary>
    public sealed class CommandPreview : Panel
    {
        private readonly TextBox text = new()
        {
            ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, Font = Theme.Mono(10f),
            BackColor = Theme.SurfaceAlt, ForeColor = Theme.TextPrimary, ScrollBars = ScrollBars.None
        };
        private bool valid;

        public CommandPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 76;
            Controls.Add(text);
        }

        public bool IsValid => valid;
        public string Command => valid ? text.Text : "";

        public void Show(string command, bool isValid)
        {
            valid = isValid;
            text.Text = command;
            text.ForeColor = isValid ? Theme.TextPrimary : Theme.TextSecondary;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            text.SetBounds(16, 12, Math.Max(20, Width - 32), Math.Max(20, Height - 24));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
            Theme.FillRound(e.Graphics, ClientRectangle, Theme.SurfaceAlt, Theme.Border);
        }
    }
}
