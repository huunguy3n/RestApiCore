using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Business
{
    /// <summary>XML v3 sinh ra và ghi chú cho người xem lại; Valid = nạp được bằng ImportFormLoader</summary>
    public sealed record UpgradeResult(string Xml, IReadOnlyList<string> Notes, bool Valid);

    /// <summary>
    /// ColumnsOf trả về tên cột (không phân biệt hoa thường) của một bảng FBO, null nếu không có bảng/không tra được.
    /// Có thì tách khối gốc/khối con của chứng từ theo cột thật của m??$000000 / d??$000000
    /// </summary>
    public sealed record UpgradeOptions(Func<string, IReadOnlySet<string>?>? ColumnsOf = null);

    /// <summary>
    /// Chuyển form FastAPI 2.4.7 (v2) sang &lt;import&gt; v3. Form v2 dùng chính &lt;import xmlns="urn:schemas-fast-com:fast-api"&gt;
    /// nhưng chỉ có MỘT &lt;fields type=… table=…&gt; phẳng (master và detail trộn chung, JSON được JsonFlattener làm phẳng),
    /// một &lt;query&gt; gọi proc api_* đọc bảng tạm #_data, và nạp chung qua entity SYSTEM "Include\*.txt".
    /// Kết quả là bản gợi ý: tách khối, đặt tên camelCase, justContent thành check; processing phải viết lại (v3 không gọi api_*)
    /// </summary>
    public static partial class V2FormUpgrader
    {
        private static readonly XNamespace Ns = ImportFormLoader.Ns;

        [GeneratedRegex(@"api_ProcessDataFor_m(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex VoucherProc();

        [GeneratedRegex(@"\bapi_\w+", RegexOptions.IgnoreCase)]
        private static partial Regex ApiProc();

        [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
        private static partial Regex WordBoundary();

        /// <summary>Nạp file v2, phân giải entity SYSTEM tương đối theo thư mục của file (Include\Irregular.txt…)</summary>
        public static XDocument Load(string path)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = new XmlUrlResolver(),
                IgnoreComments = false
            };
            using XmlReader reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader);
        }

        /// <summary>v2: &lt;fields&gt; mang type và không có name; v3 khai type ở &lt;import&gt; và mọi khối đều có name</summary>
        public static bool IsV2(XDocument document) =>
            document.Root is { } root && root.Name == Ns + "import" && root.Attribute("type") == null
                && root.Element(Ns + "fields") is { } fields && fields.Attribute("type") != null && fields.Attribute("name") == null;

        public static UpgradeResult Upgrade(string name, XDocument v2, UpgradeOptions? options = null)
        {
            if (!IsV2(v2))
            {
                throw new ArgumentException($"{name}: không phải form v2 (<import> có <fields type=… table=…> không tên khối)");
            }

            XElement root = v2.Root!;
            XElement source = root.Element(Ns + "fields")!;
            List<string> notes = [];

            string type = source.Attribute("type")!.Value.Trim();
            string voucherCode = source.Attribute("voucherCode")?.Value.Replace("'", "").Trim() ?? "";
            string table = source.Attribute("table")?.Value.Replace("'", "").Trim() ?? "";
            string query = root.Element(Ns + "query")?.Element(Ns + "text")?.Value ?? "";

            List<V2Field> fields = ReadFields(source, type, notes);

            List<Block> blocks = Split(type, table, query, fields, options, notes);

            ImportKey(blocks, type, notes);

            XElement import = new(Ns + "import",
                new XAttribute("type", type),
                new XAttribute("jsonNaming", NamingConvention.CamelCase),
                new XAttribute("columnNaming", NamingConvention.SnakeCase));
            if (voucherCode.Length > 0)
            {
                import.Add(new XAttribute("voucherCode", voucherCode));
            }

            List<int> categories = [];
            for (int i = 0; i < blocks.Count; i++)
            {
                XElement element = ToElement(blocks[i], i + 1, notes);
                if (element.Elements().Any(f => f.Attribute("check") != null)) { categories.Add(i + 1); }
                import.Add(element);
            }
            if (categories.Count > 0)
            {
                string call = string.Join(' ', categories.Select(n => $"@@checking({n})"));
                import.Add(new XElement(Ns + "query", new XAttribute("action", "checking"), new XElement(Ns + "text", new XCData(" " + call + " "))));
            }
            import.Add(new XElement(Ns + "query", new XAttribute("action", "processing"), new XElement(Ns + "text", new XCData(Processing(name, query, blocks, notes)))));

            notes.Add("Tên field JSON đổi sang camelCase (v2 phân biệt hoa thường: YourId → yourId): đối tác phải gửi đúng tên mới");

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XDocumentType("import", null, null, "\n  <!ENTITY And \"&#38;#13;\">\n"),
                new XComment($" Chuyển tự động từ form v2 {name}.xml bằng Fast.Api.Cli upgrade. Xem lại các ghi chú, viết lại processing trước khi dùng "),
                import);

            string xml = Write(document);
            bool valid = true;
            try
            {
                notes.AddRange(ImportFormLoader.Parse(name, XDocument.Parse(xml)).Warnings.Select(w => "nạp thử: " + w));
            }
            catch (ImportFormException ex)
            {
                valid = false;
                notes.Add($"XML v3 chưa nạp được: {ex.Reason}");
            }
            return new UpgradeResult(xml, notes.Distinct().ToList(), valid);
        }

        // ---- Đọc field v2 ----

        private sealed class V2Field
        {
            public required string Name { get; init; }
            public required string FastName { get; init; }
            public required XElement Source { get; init; }
            public string? ExternalTable { get; init; }
            public string JsonName { get; set; } = "";
        }

        private static List<V2Field> ReadFields(XElement source, string type, List<string> notes)
        {
            List<V2Field> result = [];
            foreach (XElement field in source.Elements(Ns + "field"))
            {
                string name = field.Attribute("name")?.Value.Trim() ?? "";
                if (name.Length == 0)
                {
                    notes.Add("bỏ một <field> không có name");
                    continue;
                }

                // cacheID: tham số lấy tiếp trang của v2. v3 phân trang bằng <paging>, không nhận field này
                if (type == "Report" && name.Equals("cacheID", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add("bỏ field cacheID (v2 dùng để lấy tiếp phần dữ liệu còn lại); v3 phân trang bằng <paging pageSize key>, khai thêm nếu form trả nhiều dòng");
                    continue;
                }

                bool hidden = IsTrue(field.Attribute("hidden"));
                string fast = field.Attribute("fastName")?.Value.Trim() is { Length: > 0 } f ? f : name;
                // v2: field hidden lấy tên = fastName (xem ApiField)
                string baseName = hidden ? fast : name;

                string? externalTable = null;
                if (IsTrue(field.Attribute("external")) && name.IndexOf('_') > 0)
                {
                    externalTable = name[..name.IndexOf('_')];
                }

                if (!NamingConvention.IsSnakeCase(fast))
                {
                    string fixedName = Snake(fast);
                    notes.Add($"{name}: fastName '{fast}' không phải snake_case, đổi thành '{fixedName}' — kiểm tra cột thật");
                    fast = fixedName;
                }
                result.Add(new V2Field { Name = baseName, FastName = fast, Source = field, ExternalTable = externalTable });
            }
            return result;
        }

        // ---- Tách khối ----

        private sealed class Block
        {
            public required string Name { get; init; }
            public string Table { get; set; } = "";
            public string Key { get; set; } = "";
            public List<V2Field> Fields { get; } = [];
        }

        private static List<Block> Split(string type, string table, string query, List<V2Field> fields, UpgradeOptions? options, List<string> notes)
        {
            string rootName = type == "Report" ? "filter" : "master";
            var master = new Block { Name = rootName };
            List<Block> blocks = [master];

            if (type == "Report")
            {
                // v2 khai table cho Report chỉ để điền mặc định; cột điều kiện lọc không nhất thiết thuộc bảng đó
                if (table.Length > 0)
                {
                    notes.Add($"Report: bỏ table '{table}' ở khối {rootName}, kiểu và độ rộng khai tường minh ở từng field");
                }
            }
            else if (type == "Voucher")
            {
                SplitVoucher(master, blocks, query, fields, options, notes);
                SplitExternal(blocks, fields);
                return blocks;
            }
            else
            {
                master.Table = table;
            }

            master.Fields.AddRange(fields);
            SplitExternal(blocks, fields);
            return blocks;
        }

        /// <summary>
        /// Chứng từ: field v2 không nói thuộc master hay detail. Tra cột thật của m??$000000 / d??$000000 (số lấy từ
        /// api_ProcessDataFor_m??); cột có ở cả hai bảng hoặc không có ở bảng nào thì theo field trước đó
        /// </summary>
        private static void SplitVoucher(Block master, List<Block> blocks, string query, List<V2Field> fields, UpgradeOptions? options, List<string> notes)
        {
            Match proc = VoucherProc().Match(query);
            if (!proc.Success)
            {
                master.Fields.AddRange(fields);
                if (fields.Count > 1)
                {
                    notes.Add("không thấy api_ProcessDataFor_m?? trong query nên không biết bảng chứng từ: toàn bộ field vào khối master, tự tách khối detail");
                }
                return;
            }

            string number = proc.Groups[1].Value;
            string masterTable = $"m{number}$000000", detailTable = $"d{number}$000000";
            IReadOnlySet<string>? masterColumns = options?.ColumnsOf?.Invoke(masterTable);
            IReadOnlySet<string>? detailColumns = options?.ColumnsOf?.Invoke(detailTable);

            if (masterColumns == null || detailColumns == null)
            {
                master.Fields.AddRange(fields);
                notes.Add($"không đọc được cột {masterTable}/{detailTable} nên toàn bộ field vào khối master: chạy lại với --config <appsettings.Local.json> để tách master/detail theo cột thật, hoặc tự tách");
                return;
            }

            master.Table = masterTable;
            var detail = new Block { Name = "detail", Table = detailTable };
            Block previous = master;
            foreach (V2Field field in fields)
            {
                bool inMaster = masterColumns.Contains(field.FastName), inDetail = detailColumns.Contains(field.FastName);
                Block target = inMaster == inDetail ? previous : inDetail ? detail : master;
                if (inMaster != inDetail) { previous = target; }
                target.Fields.Add(field);
            }

            if (detail.Fields.Count > 0)
            {
                blocks.Add(detail);
                notes.Add($"Tách theo cột {masterTable}/{detailTable}. Detail: {string.Join(", ", detail.Fields.Select(f => f.FastName))}. Xem lại field có ở cả hai bảng hoặc không có ở bảng nào (theo field đứng trước)");
            }
            else
            {
                notes.Add($"không có field nào thuộc {detailTable}: chỉ có khối master");
            }
        }

        /// <summary>v2 external="true": field "TaxDetail_ReportForm" thuộc bảng con thứ ba trở đi (JSON mảng TaxDetail)</summary>
        private static void SplitExternal(List<Block> blocks, List<V2Field> fields)
        {
            foreach (IGrouping<string, V2Field> group in fields.Where(f => f.ExternalTable != null).GroupBy(f => f.ExternalTable!, StringComparer.OrdinalIgnoreCase))
            {
                foreach (V2Field field in group)
                {
                    foreach (Block block in blocks) { block.Fields.Remove(field); }
                }
                var child = new Block { Name = CamelCase(group.Key) };
                child.Fields.AddRange(group);
                blocks.Add(child);
            }
        }

        // ---- Khóa khối gốc ----

        private static void ImportKey(List<Block> blocks, string type, List<string> notes)
        {
            Block root = blocks[0];
            if (type == "Report")
            {
                return;
            }
            V2Field? key = root.Fields.FirstOrDefault(f => IsTrue(f.Source.Attribute("isPrimaryKey")))
                ?? (type == "Voucher" ? root.Fields.FirstOrDefault(f => f.FastName == "id_doitac") : null);
            if (key != null)
            {
                root.Key = key.Name;
            }
            else if (blocks.Count > 1)
            {
                notes.Add("không xác định được key khối gốc (không có isPrimaryKey/id_doitac): khai key bằng tay");
            }
        }

        // ---- Sinh XML ----

        private static XElement ToElement(Block block, int category, List<string> notes)
        {
            XElement element = new(Ns + "fields", new XAttribute("name", block.Name));
            if (block.Table.Length > 0) { element.Add(new XAttribute("table", block.Table)); }

            // Tên JSON: camelCase, duy nhất trong khối
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            foreach (V2Field field in block.Fields)
            {
                string raw = field.ExternalTable != null ? field.Name[(field.ExternalTable.Length + 1)..] : field.Name;
                string json = CamelCase(raw);
                for (int n = 2; !used.Add(json); n++)
                {
                    notes.Add($"{block.Name}.{raw}: trùng tên sau khi đổi camelCase, đặt '{json}{n}'");
                    json = CamelCase(raw) + n;
                }
                field.JsonName = json;
            }
            if (block.Key.Length > 0)
            {
                V2Field key = block.Fields.First(f => f.Name == block.Key);
                element.Add(new XAttribute("key", key.JsonName));
            }

            foreach (V2Field field in block.Fields)
            {
                element.Add(ConvertField(block, field, category, notes));
            }
            return element;
        }

        private static XElement ConvertField(Block block, V2Field field, int category, List<string> notes)
        {
            XElement s = field.Source;
            string where = $"{block.Name}.{field.Name}";
            XElement result = new(Ns + "field", new XAttribute("name", field.JsonName), new XAttribute("fastName", field.FastName));

            string v2Type = s.Attribute("type")?.Value.Trim().ToLowerInvariant() ?? "";
            string? type = v2Type switch
            {
                "" or "string" => null,
                "datetime" or "numeric" or "bool" => v2Type,
                _ => Unknown(v2Type, where, notes)
            };
            Copy(result, "type", type);

            string maxLength = s.Attribute("maxLength")?.Value ?? "";
            // v2: không khai maxLength thì mặc định 256 (ApiField)
            if (maxLength.Length == 0 && type == null && !IsTrue(s.Attribute("hidden"))) { maxLength = "256"; }
            Copy(result, "maxLength", type == null ? maxLength : null);

            foreach (string attribute in new[] { "allowNull", "isPrimaryKey", "upperCase", "hidden", "checkIrregular" })
            {
                Copy(result, attribute, s.Attribute(attribute)?.Value.Trim().ToLowerInvariant());
            }

            string? defaultValue = s.Attribute("defaultValue")?.Value.Trim();
            if (defaultValue != null)
            {
                result.Add(new XAttribute("defaultValue", defaultValue));
                if (defaultValue.Replace(" ", "").Equals("getdate()", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"{where}: defaultValue getdate() (v2 điền ngày giờ máy chủ), kiểm tra v3 xử lý giá trị này ở processing");
                }
            }

            // v2 justContent = danh sách giá trị được phép; v3 justContent là cờ bool khác nghĩa nên đổi thành check
            if (s.Attribute("justContent")?.Value is { Length: > 0 } just)
            {
                string[] values = just.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string list = string.Join(", ", values.Select(v => v.Replace("'", "''")).Select(v => type == "numeric" ? v : $"'{v}'"));
                result.Add(new XAttribute("category", category));
                result.Add(new XAttribute("check", $"{field.FastName} not in ({list})"));
                result.Add(new XAttribute("errorCode", "INVALID_FIELD"));
                notes.Add($"{where}: justContent \"{just}\" thành check {field.FastName} not in ({list}), lỗi INVALID_FIELD");
            }
            return result;
        }

        private static string? Unknown(string v2Type, string where, List<string> notes)
        {
            notes.Add($"{where}: type v2 '{v2Type}' không có ở v3, để string");
            return null;
        }

        // ---- Query ----

        /// <summary>
        /// v2 gọi proc api_* đọc #_data (một bảng phẳng); v3 không gọi api_* và có #master/#detail riêng.
        /// Không dịch máy: giữ nguyên query v2 làm chú thích, processing trả ERROR cho tới khi người viết lại
        /// </summary>
        private static string Processing(string name, string query, List<Block> blocks, List<string> notes)
        {
            string[] procs = ApiProc().Matches(query).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string tables = string.Join(", ", blocks.Select(b => "#" + b.Name));
            notes.Add($"processing: viết lại cho v3 — query v2 đọc #_data (bảng phẳng), v3 có {tables}"
                + (procs.Length > 0 ? $"; v3 không gọi proc v2 ({string.Join(", ", procs)})" : ""));
            if (query.Contains("@@update", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("processing: @@update không còn ở v3");
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"-- TODO v3: viết processing cho {name}. Query v2 dưới đây chỉ để tham khảo, KHÔNG chạy");
            foreach (string line in query.Replace("\r", "").Replace("]]>", "]] >").Split('\n'))
            {
                if (line.Trim().Length > 0) { sb.AppendLine("-- " + line.TrimEnd()); }
            }
            sb.AppendLine("SELECT 'ERROR' AS code, N'Chưa chuyển processing v2 sang v3' AS message");
            return sb.ToString();
        }

        // ---- Tiện ích ----

        /// <summary>CusID → cusID, GLDateFrom → glDateFrom, YourId → yourId, ma_nt → maNt</summary>
        public static string CamelCase(string name)
        {
            string[] words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                if (i == 0)
                {
                    // Hạ cả cụm chữ hoa đầu, chừa chữ hoa cuối nếu nó mở đầu một từ mới
                    int run = 0;
                    while (run < word.Length && char.IsUpper(word[run])) { run++; }
                    int lower = run <= 1 ? run : run == word.Length ? run : run - 1;
                    word = word[..lower].ToLowerInvariant() + word[lower..];
                }
                else
                {
                    word = char.ToUpperInvariant(word[0]) + word[1..];
                }
                sb.Append(word);
            }
            return sb.Length == 0 ? name : sb.ToString();
        }

        public static string Snake(string name) => WordBoundary().Replace(name, "_").ToLowerInvariant();

        private static bool IsTrue(XAttribute? attribute) => attribute?.Value.Trim().ToLowerInvariant() is "true" or "1";

        private static void Copy(XElement target, string attribute, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                target.Add(new XAttribute(attribute, value));
            }
        }

        private static string Write(XDocument document)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\r\n",
                Encoding = new UTF8Encoding(false),
                // Giữ CR trong thuộc tính dạng &#xD; để tách được danh sách check
                NewLineHandling = NewLineHandling.Entitize
            };
            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, settings))
            {
                document.Save(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
