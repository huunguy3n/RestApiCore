using System.Text;
using System.Xml;
using System.Xml.Linq;
using Business;
using DataAccess;
using PortalApp.Core;
using PortalApp.Ui;

namespace PortalApp.Pages
{
    /// <summary>
    /// Chuyển form FastAPI v2 sang &lt;import&gt; v3 (thay lệnh CLI "upgrade"). Tách master/detail theo cột thật qua DB của dự án (chỉ đọc).
    /// Ghi ra thư mục chọn riêng, không ghi đè App_Data\Form: người làm xem lại rồi mới đưa vào (qua WebPortal hoặc gói)
    /// </summary>
    public sealed class UpgradePage : PageBase
    {
        private readonly TextBox source = Input(), output = Input();
        private readonly CheckBox useDb = new() { Text = "Tách master/detail theo cột thật (đọc DB dự án)", AutoSize = true, Checked = true };
        private readonly CheckBox overwrite = new() { Text = "Ghi đè file đã có ở thư mục xuất", AutoSize = true };
        private readonly AppButton pickSource = new("Chọn thư mục form v2", Theme.Icon.Folder, ButtonStyle.Secondary);
        private readonly AppButton pickOutput = new("Chọn thư mục xuất", Theme.Icon.Folder, ButtonStyle.Secondary);
        private readonly AppButton run = new("Chuyển đổi", Theme.Icon.Play, ButtonStyle.Primary);
        private readonly TextBox notes = Input(multiline: true, height: 260);

        public UpgradePage(LocalProject project, ILogSink log)
            : base(project, log, Theme.Icon.Rekey, "Nâng form v2", "Chuyển XML FastAPI 2.4.7 sang <import> v3, giữ tên file. Đọc ghi chú cần xem lại trước khi dùng.")
        {
            TableLayoutPanel input = AddSection("Chuyển đổi");
            AddRow(input, "Thư mục form v2 (hoặc file .xml)", source);
            AddRow(input, "Thư mục xuất", output);
            AddRow(input, "", useDb);
            AddRow(input, "", overwrite);
            AddWide(input, Buttons(pickSource, pickOutput, run));
            TableLayoutPanel report = AddSection("Ghi chú");
            AddWide(report, notes);
            notes.ReadOnly = true;

            pickSource.Click += (_, _) => Pick(source);
            pickOutput.Click += (_, _) => Pick(output);
            run.Click += async (_, _) => await RunAsync(run, async () => notes.Text = await Task.Run(Convert));
        }

        private void Pick(TextBox target)
        {
            using FolderBrowserDialog dialog = new();
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dialog.SelectedPath;
            }
        }

        private string Convert()
        {
            string input = source.Text.Trim(), outDirectory = output.Text.Trim();
            List<string> files = Directory.Exists(input)
                ? [.. Directory.EnumerateFiles(input, "*.xml").Order(StringComparer.OrdinalIgnoreCase)]
                : File.Exists(input) ? [input] : [];
            if (files.Count == 0 || outDirectory.Length == 0)
            {
                return "Chọn thư mục/file form v2 và thư mục xuất.";
            }
            if (string.Equals(Path.GetFullPath(outDirectory).TrimEnd('\\'), Path.GetFullPath(Project.FormDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return "Không xuất thẳng vào App_Data\\Form: chọn thư mục khác, xem lại rồi mới đưa vào.";
            }

            using TableColumnCatalog? catalog = useDb.Checked && Project.IsConfigured ? TableColumnCatalog.Open(Project.Database()) : null;
            UpgradeOptions options = new(catalog == null ? null : catalog.ColumnsOf);
            StringBuilder report = new();
            int converted = 0, failed = 0;
            Directory.CreateDirectory(outDirectory);
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                XDocument document;
                try
                {
                    document = V2FormUpgrader.Load(file);
                }
                catch (XmlException ex)
                {
                    report.AppendLine($"{name}: XML không hợp lệ: {ex.Message}");
                    failed++;
                    continue;
                }
                if (!V2FormUpgrader.IsV2(document))
                {
                    report.AppendLine($"{name}: không phải form v2, bỏ qua");
                    continue;
                }
                UpgradeResult result = V2FormUpgrader.Upgrade(name, document, options);
                converted++;
                report.AppendLine($"== {name}{(result.Valid ? "" : " (CHƯA NẠP ĐƯỢC, cần sửa tay)")}");
                foreach (string note in result.Notes)
                {
                    report.AppendLine("  - " + note);
                }
                string target = Path.Combine(outDirectory, name + ".xml");
                if (File.Exists(target) && !overwrite.Checked)
                {
                    report.AppendLine($"  {target} đã có, bỏ qua (chọn ghi đè nếu cần)");
                    failed++;
                    continue;
                }
                File.WriteAllText(target, result.Xml, new UTF8Encoding(true));
                failed += result.Valid ? 0 : 1;
                report.AppendLine("  → " + target);
            }
            report.AppendLine().AppendLine($"Đã chuyển {converted} form, {failed} cần xử lý tay.");
            return report.ToString().Replace("\n", Environment.NewLine);
        }
    }
}
