using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Business
{
    /// <summary>Bảng và cột mã của một controller trong Update.xml v2, vd Customer → dmkh.ma_kh</summary>
    public sealed record ControllerLookup(string Table, string Column);

    /// <summary>XML v3 sinh ra và ghi chú cho người xem lại; Valid = nạp được bằng ImportFormLoader</summary>
    public sealed record UpgradeResult(string Xml, IReadOnlyList<string> Notes, bool Valid);

    /// <summary>
    /// Chuyển form v2 (&lt;form xmlns="urn:schemas-fast-com:rest-api"&gt;) sang &lt;import&gt; v3 (KE_HOACH 2.5).
    /// Giữ tên file; khối đầu là khối gốc; &lt;reference&gt; thành check tồn tại mã (đối tác gửi mã FBO thay mã của họ);
    /// bỏ @@update; đổi tên biến v2. Kết quả là bản gợi ý, người xem lại rồi mới dùng
    /// </summary>
    public static partial class V2FormUpgrader
    {
        public static readonly XNamespace V2 = "urn:schemas-fast-com:rest-api";
        private static readonly XNamespace ImportNs = ImportFormLoader.Ns;

        [GeneratedRegex(@"set\s+@@reference\s*=\s*b\.(\w+)\s+from\s+@@table\s+a\s+join\s+([\w$]+)\s+b", RegexOptions.IgnoreCase)]
        private static partial Regex UpdateScript();

        [GeneratedRegex(@"@@update\((\d+)\)", RegexOptions.IgnoreCase)]
        private static partial Regex UpdateToken();

        [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
        private static partial Regex WordBoundary();

        [GeneratedRegex(@"'@@(app|sys)DatabaseName'", RegexOptions.IgnoreCase)]
        private static partial Regex QuotedDatabaseName();

        public static bool IsV2(XDocument document) => document.Root?.Name == V2 + "form";

        /// <summary>
        /// Đọc Update.xml v2: script dạng "update @@table set @@reference = b.ma_kh from @@table a join dmkh b …"
        /// </summary>
        public static Dictionary<string, ControllerLookup> ReadControllers(XDocument update)
        {
            Dictionary<string, ControllerLookup> controllers = new(StringComparer.OrdinalIgnoreCase);
            foreach (XElement controller in update.Root?.Elements().Where(e => e.Name.LocalName == "controller") ?? [])
            {
                Match match = UpdateScript().Match(controller.Value);
                string? name = controller.Attribute("name")?.Value;
                if (match.Success && !string.IsNullOrEmpty(name))
                {
                    controllers[name] = new ControllerLookup(match.Groups[2].Value, match.Groups[1].Value);
                }
            }
            return controllers;
        }

        public static UpgradeResult Upgrade(string name, XDocument v2, IReadOnlyDictionary<string, ControllerLookup> controllers)
        {
            XElement form = v2.Root is XElement root && root.Name == V2 + "form"
                ? root
                : throw new ArgumentException($"{name}: không phải form v2 (<form xmlns=\"{V2}\">)");

            List<string> notes = [];
            List<XElement> structs = form.Elements(V2 + "struct").ToList();

            XElement import = new(ImportNs + "import", new XAttribute("type", form.Attribute("type")?.Value ?? "List"));
            if (form.Attribute("id")?.Value is { Length: > 0 } voucherCode)
            {
                import.Add(new XAttribute("voucherCode", voucherCode));
            }

            string rootKey = FindRootKey(structs, notes);
            string? rootKeyMapping = null;

            for (int i = 0; i < structs.Count; i++)
            {
                XElement block = ConvertStruct(structs[i], i == 0, rootKey, ref rootKeyMapping, controllers, notes);
                import.Add(block);
            }

            foreach (XElement query in form.Element(V2 + "queries")?.Elements(V2 + "query") ?? [])
            {
                string action = (query.Attribute("action")?.Value ?? "processing").ToLowerInvariant();
                string text = ConvertQuery(query.Element(V2 + "text")?.Value ?? query.Value, notes);
                import.Add(new XElement(ImportNs + "query", new XAttribute("action", action), new XElement(ImportNs + "text", new XCData(text))));
            }

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XDocumentType("import", null, null, "\n  <!ENTITY And \"&#38;#13;\">\n"),
                new XComment($" Chuyển tự động từ form v2 {name}.xml bằng Fast.Api.Cli upgrade. Xem lại các ghi chú trước khi dùng "),
                import);

            string xml = Write(document);
            bool valid = true;
            try
            {
                // Cảnh báo lúc nạp (vd category không được @@checking gọi) cũng là việc cần xem lại
                notes.AddRange(ImportFormLoader.Parse(name, XDocument.Parse(xml)).Warnings.Select(w => "nạp thử: " + w));
            }
            catch (ImportFormException ex)
            {
                valid = false;
                notes.Add($"XML v3 chưa nạp được: {ex.Reason}");
            }
            return new UpgradeResult(xml, notes.Distinct().ToList(), valid);
        }

        /// <summary>
        /// Khóa khối gốc: field isPrimaryKey đầu tiên; không có thì field cùng tên có ở mọi khối
        /// </summary>
        private static string FindRootKey(List<XElement> structs, List<string> notes)
        {
            if (structs.Count == 0)
            {
                notes.Add("form v2 không có <struct>");
                return string.Empty;
            }

            string? key = Fields(structs[0]).FirstOrDefault(f => IsTrue(f.Attribute("isPrimaryKey")))?.Attribute("name")?.Value;
            if (key == null && structs.Count > 1)
            {
                key = Fields(structs[0]).Select(f => f.Attribute("name")?.Value)
                    .FirstOrDefault(n => n != null && structs.Skip(1).All(s => Fields(s).Any(f => f.Attribute("name")?.Value == n)));
                if (key != null)
                {
                    notes.Add($"khối gốc không có isPrimaryKey, chọn '{key}' (có ở mọi khối) làm key");
                }
                else
                {
                    notes.Add("không xác định được key khối gốc: khai key cho khối gốc bằng tay");
                }
            }
            return key ?? string.Empty;
        }

        /// <summary>Tên JSON v3 là camelCase: v2 hay đặt PascalCase nên hạ chữ đầu</summary>
        private static string JsonName(string name) => name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;

        private static XElement ConvertStruct(XElement source, bool isRoot, string rootKey, ref string? rootKeyMapping,
            IReadOnlyDictionary<string, ControllerLookup> controllers, List<string> notes)
        {
            string blockName = source.Attribute("name")?.Value ?? "";
            XElement block = new(ImportNs + "fields", new XAttribute("name", blockName));
            if (source.Attribute("baseTable")?.Value is { Length: > 0 } table)
            {
                block.Add(new XAttribute("table", table));
            }
            if (isRoot && rootKey.Length > 0)
            {
                block.Add(new XAttribute("key", JsonName(rootKey)));
            }

            List<XElement> fields = Fields(source).ToList();
            // Field ẩn mapping="" có tên trùng <reference name> là chỗ chứa mã đã tra ở v2: v3 gộp vào field có reference
            HashSet<string> referenceTargets = new(fields
                .Select(f => f.Element(V2 + "reference")?.Attribute("name")?.Value)
                .OfType<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (XElement field in fields)
            {
                string fieldName = field.Attribute("name")?.Value ?? "";
                string mapping = field.Attribute("mapping")?.Value ?? "";

                if (mapping.Length == 0 && referenceTargets.Contains(fieldName))
                {
                    notes.Add($"{blockName}.{fieldName}: bỏ field ẩn chứa mã tra từ <reference>, đã gộp vào field có reference");
                    continue;
                }
                if (isRoot && fieldName == rootKey)
                {
                    rootKeyMapping = mapping;
                }
                else if (!isRoot && fieldName == rootKey && mapping == rootKeyMapping)
                {
                    // v3 tự thêm khóa khối gốc vào khối con
                    continue;
                }

                block.Add(ConvertField(blockName, field, controllers, notes));
            }
            return block;
        }

        private static XElement ConvertField(string blockName, XElement field, IReadOnlyDictionary<string, ControllerLookup> controllers, List<string> notes)
        {
            string fieldName = field.Attribute("name")?.Value ?? "";
            string mapping = field.Attribute("mapping")?.Value ?? "";
            XElement result = new(ImportNs + "field", new XAttribute("name", fieldName));

            List<(string Check, string ErrorCode, int Index)> checks = [];
            XElement? reference = field.Element(V2 + "reference");

            if (reference != null)
            {
                string target = reference.Attribute("name")?.Value ?? fieldName;
                string controller = reference.Attribute("controller")?.Value ?? "";
                int index = int.TryParse(reference.Attribute("index")?.Value, out int i) ? i : 0;
                result.Add(new XAttribute("fastName", target), new XAttribute("upperCase", "true"));

                if (controllers.TryGetValue(controller, out ControllerLookup? lookup))
                {
                    checks.Add(($"{target} not in (select {lookup.Column} from {lookup.Table})", ErrorCodeOf(controller), index));
                    notes.Add($"{blockName}.{fieldName}: <reference controller=\"{controller}\"> thành check tồn tại {lookup.Table}.{lookup.Column}. "
                        + $"Đối tác phải gửi mã FBO ({target}) thay mã của họ ({mapping}); thêm điều kiện status nếu cần");
                }
                else
                {
                    notes.Add($"{blockName}.{fieldName}: controller '{controller}' không có trong Update.xml, chưa sinh check tồn tại mã");
                }
            }
            else
            {
                if (mapping.Length == 0)
                {
                    notes.Add($"{blockName}.{fieldName}: v2 không có mapping, dùng fastName = name");
                }
                result.Add(new XAttribute("fastName", mapping.Length > 0 ? mapping : fieldName));
            }

            string v2Type = field.Attribute("type")?.Value ?? "";
            string? type = v2Type.ToLowerInvariant() switch
            {
                "" or "string" => null,
                "datetime" => "datetime",
                "decimal" => "numeric",
                "int" => "int",
                "boolean" => "bool",
                "unixtimestamp" => "unixtime",
                _ => Unknown()
            };
            string? Unknown()
            {
                notes.Add($"{blockName}.{fieldName}: type v2 '{v2Type}' không có ở v3, để string");
                return null;
            }

            Copy(result, "type", type);
            Copy(result, "format", field.Attribute("dataFormatString")?.Value);
            if (reference == null)
            {
                Copy(result, "maxLength", field.Attribute("maxLength")?.Value);
            }
            Copy(result, "allowNull", field.Attribute("allowNulls")?.Value?.ToLowerInvariant());
            foreach (string attribute in new[] { "isPrimaryKey", "upperCase", "hidden", "defaultValue", "checkIrregular", "justContent" })
            {
                if (result.Attribute(attribute) == null)
                {
                    Copy(result, attribute, field.Attribute(attribute)?.Value);
                }
            }

            foreach (XElement check in field.Elements(V2 + "check"))
            {
                checks.Add((check.Attribute("condition")?.Value ?? "", check.Attribute("errorCode")?.Value ?? "INVALID_FIELD",
                    int.TryParse(check.Attribute("index")?.Value, out int index) ? index : 0));
            }

            if (checks.Count > 0)
            {
                result.Add(new XAttribute("category", checks[0].Index));
                if (checks.Select(c => c.Index).Distinct().Count() > 1)
                {
                    notes.Add($"{blockName}.{fieldName}: các check v2 khác index, v3 một field một category: dùng {checks[0].Index}");
                }
                result.Add(new XAttribute("check", string.Join('\r', checks.Select(c => c.Check))));
                result.Add(new XAttribute("errorCode", string.Join('\r', checks.Select(c => c.ErrorCode))));
            }
            return result;
        }

        /// <summary>
        /// Query v2 → v3: bỏ @@update(n); @@userName v2 (tài khoản API) → @@apiUserName; @@userERP → @@userName; @@id → @@voucherCode
        /// </summary>
        public static string ConvertQuery(string text, List<string> notes)
        {
            string result = UpdateToken().Replace(text, m =>
            {
                notes.Add($"query: bỏ @@update({m.Groups[1].Value}), <reference> đã thành check tồn tại mã");
                return $"/* v2 update({m.Groups[1].Value}) đã bỏ */";
            });

            result = ReplaceVariable(result, "userName", "apiUserName", notes, "@@userName v2 là tên tài khoản API → @@apiUserName (ở v3 @@userName là user FBO)");
            result = ReplaceVariable(result, "userERP", "userName", notes, "@@userERP → @@userName");
            result = ReplaceVariable(result, "id", "voucherCode", notes, "@@id → @@voucherCode");

            if (QuotedDatabaseName().IsMatch(result))
            {
                notes.Add("query: '@@appDatabaseName' / '@@sysDatabaseName' trong dấu nháy: v3 thay bằng [tên] (định danh), "
                    + "truyền làm chuỗi thì sửa thành tên trực tiếp hoặc DB_NAME()");
            }
            return result;
        }

        private static string ReplaceVariable(string text, string from, string to, List<string> notes, string note)
        {
            var pattern = new Regex($@"@@{from}\b", RegexOptions.IgnoreCase);
            if (!pattern.IsMatch(text))
            {
                return text;
            }
            notes.Add("query: " + note);
            return pattern.Replace(text, "@@" + to);
        }

        /// <summary>Customer → CUSTOMER_NOT_FOUND, SaleEmployee → SALE_EMPLOYEE_NOT_FOUND</summary>
        public static string ErrorCodeOf(string controller) => WordBoundary().Replace(controller, "_").ToUpperInvariant() + "_NOT_FOUND";

        private static IEnumerable<XElement> Fields(XElement block) => block.Elements(V2 + "field");

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
