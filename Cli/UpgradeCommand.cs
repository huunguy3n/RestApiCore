using System.Text;
using System.Xml;
using System.Xml.Linq;
using Business;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cli
{
    /// <summary>
    /// Chuyển form FastAPI 2.4.7 (v2, &lt;import&gt; phẳng + Include\*.txt) sang &lt;import&gt; v3. Không có --out thì in ra màn hình;
    /// có --out thì ghi file cùng tên. --config (chỉ đọc) cho phép tách master/detail theo cột thật của bảng chứng từ
    /// </summary>
    public static class UpgradeCommand
    {
        public static int Run(string[] args)
        {
            string? input = null, configPath = null, outDirectory = null;
            bool force = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": configPath = CliArgs.Next(args, ref i); break;
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
            if (configPath != null && !File.Exists(configPath))
            {
                throw new ArgumentException($"Không có file cấu hình {configPath}");
            }

            List<string> files = Directory.Exists(input)
                ? [.. Directory.EnumerateFiles(input, "*.xml").Order(StringComparer.OrdinalIgnoreCase)]
                : File.Exists(input) ? [input] : throw new ArgumentException($"Không có {input}");

            using ColumnCatalog? catalog = configPath == null ? null : ColumnCatalog.Open(configPath);
            Console.WriteLine(catalog == null
                ? "Không có --config: chứng từ sẽ không tách được master/detail theo cột thật"
                : "Tách master/detail theo cột thật qua --config (chỉ đọc)");
            var options = new UpgradeOptions(catalog == null ? null : catalog.ColumnsOf);

            int failed = 0, converted = 0;
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
                    Console.Error.WriteLine($"{name}: XML không hợp lệ: {ex.Message}");
                    failed++;
                    continue;
                }

                if (!V2FormUpgrader.IsV2(document))
                {
                    Console.WriteLine($"{name}: không phải form v2, bỏ qua");
                    continue;
                }

                UpgradeResult result = V2FormUpgrader.Upgrade(name, document, options);
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

        /// <summary>Tra cột bảng qua INFORMATION_SCHEMA, chỉ đọc, nhớ kết quả theo bảng</summary>
        private sealed class ColumnCatalog : IDisposable
        {
            private readonly SqlConnection connection;
            private readonly Dictionary<string, IReadOnlySet<string>?> cache = new(StringComparer.OrdinalIgnoreCase);

            private ColumnCatalog(SqlConnection connection) => this.connection = connection;

            public static ColumnCatalog Open(string configPath)
            {
                IConfiguration config = new ConfigurationBuilder()
                    .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                    .AddEnvironmentVariables()
                    .Build();
                string connectionString = config.GetConnectionString("appConnectionString") ?? "";
                if (connectionString.Length == 0)
                {
                    throw new ArgumentException("Thiếu connectionStrings:appConnectionString");
                }
                var connection = new SqlConnection(connectionString);
                connection.Open();
                return new ColumnCatalog(connection);
            }

            public IReadOnlySet<string>? ColumnsOf(string table)
            {
                if (cache.TryGetValue(table, out IReadOnlySet<string>? known))
                {
                    return known;
                }

                using var command = new SqlCommand("SELECT RTRIM(COLUMN_NAME) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table", connection);
                command.Parameters.AddWithValue("@table", table);
                HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        columns.Add(reader.GetString(0));
                    }
                }
                return cache[table] = columns.Count > 0 ? columns : null;
            }

            public void Dispose() => connection.Dispose();
        }
    }
}
