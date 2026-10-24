namespace PortalApp.Ui
{
    /// <summary>Hộp xác nhận cho thao tác có tác động thật. Nút mặc định (Enter) là Hủy để tránh bấm nhầm.</summary>
    public sealed class ConfirmationDialog : Form
    {
        private ConfirmationDialog(string title, string message, string confirmText)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            BackColor = Theme.Surface;
            ClientSize = new Size(460, 190);
            Font = Theme.Body();

            Controls.Add(new Label { Text = Theme.Icon.Warn, Font = Theme.Icons(26f), ForeColor = Theme.Warning, AutoSize = true, Left = 24, Top = 24 });
            Controls.Add(new Label { Text = title, Font = Theme.Body(12f, FontStyle.Bold), ForeColor = Theme.TextPrimary, AutoSize = true, Left = 76, Top = 22 });
            Controls.Add(new Label { Text = message, ForeColor = Theme.TextSecondary, Left = 76, Top = 54, Width = 360, Height = 70 });

            var cancel = new AppButton("Hủy", Theme.Icon.Close, ButtonStyle.Secondary) { Left = 220, Top = 134, Width = 100 };
            var confirm = new AppButton(confirmText, Theme.Icon.Check, ButtonStyle.Danger) { Left = 330, Top = 134, Width = 112 };
            cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
            confirm.Click += (_, _) => DialogResult = DialogResult.OK;
            Controls.AddRange([cancel, confirm]);
            ActiveControl = cancel;
        }

        public static bool Ask(IWin32Window owner, string title, string message, string confirmText)
        {
            using var dialog = new ConfirmationDialog(title, message, confirmText);
            return dialog.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
