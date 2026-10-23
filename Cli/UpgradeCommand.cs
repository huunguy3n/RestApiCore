using System.Text;
using System.Xml;
using System.Xml.Linq;
using Business.V3;

namespace Cli
{
    /// <summary>
    /// Chuyển form v2 sang &lt;import&gt; v3 (KE_HOACH_V3 2.5). Không có --out thì in ra màn hình; có --out thì ghi file cùng tên
    /// </summary>
    public static class UpgradeCommand
    {
        public static int Run(string[] args)
        {
            string? input = null, updatePath = null, outDirectory = null;
            bool force = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--update": updatePath = CliArgs.Next(args, ref i); break;
                    case "--out": outDirectory = CliArgs.Next(args, ref i); break;
                    case "--force": force = true; break;
                    default:
                        if (input != null || args[i].StartsWith("--"))
                        {
                            throw new ArgumentException($"Tham số không hỗ trợ: {args[i]}");
                        }
                        input = args[i];
                        break;
                }
            }

            if (input == null)
            {
                throw new ArgumentException("Thiếu file hoặc thư mục form v2");
            }

            List<string> files = Directory.Exists(input)
                ? [.. Directory.EnumerateFiles(input, "*.xml").Order(StringComparer.OrdinalIgnoreCase)]
                : File.Exists(input) ? [input] : throw new ArgumentException($"Không có {input}");

            // Mặc định Update.xml ở App_Data/Options cạnh thư mục Form
            updatePath ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!, "..", "Options", "Update.xml");
            Dictionary<string, ControllerLookup> controllers = File.Exists(updatePath)
                ? V2FormUpgrader.ReadControllers(XDocument.Load(updatePath))
                : [];
            Console.WriteLine(File.Exists(updatePath)
                ? $"Update.xml: {Path.GetFullPath(updatePath)} ({controllers.Count} controller: {string.Join(", ", controllers.Select(c => $"{c.Key} → {c.Value.Table}.{c.Value.Column}"))})"
                : $"Không có Update.xml ({updatePath}): <reference> sẽ không sinh check tồn tại mã");

            int failed = 0, converted = 0;
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                XDocument document;
                try
                {
                    document = XDocument.Load(file, LoadOptions.None);
                }
                catch (XmlException ex)
                {
                    Console.Error.WriteLine($"{name}: XML không hợp lệ: {ex.Message}");
                    failed++;
                    continue;
                }

                if (!V2FormUpgrader.IsV2(document))
                {
                    Console.WriteLine($"{name}: không phải form v2, bỏ qua");
                    continue;
                }

                UpgradeResult result = V2FormUpgrader.Upgrade(name, document, controllers);
                converted++;
                if (!result.Valid)
                {
                    failed++;
                }

                Console.WriteLine();
                Console.WriteLine($"== {name}{(result.Valid ? "" : " (CHƯA NẠP ĐƯỢC, cần sửa tay)")}");
                foreach (string note in result.Notes)
                {
                    Console.WriteLine($"  - {note}");
                }

                if (outDirectory == null)
                {
                    Console.WriteLine(result.Xml);
                    continue;
                }

                Directory.CreateDirectory(outDirectory);
                string target = Path.Combine(outDirectory, name + ".xml");
                if (File.Exists(target) && !force)
                {
                    Console.Error.WriteLine($"  {target} đã có, thêm --force để ghi đè");
                    failed++;
                    continue;
                }
                File.WriteAllText(target, result.Xml, new UTF8Encoding(true));
                Console.WriteLine($"  → {target}");
            }

            Console.WriteLine();
            Console.WriteLine($"Đã chuyển {converted} form, {failed} cần xử lý tay.");
            return failed == 0 ? 0 : 1;
        }
    }
}
