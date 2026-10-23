using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Models;

namespace Business
{
    /// <summary>
    /// Lỗi nạp form: file bị bỏ qua, ghi log, các form khác vẫn chạy
    /// </summary>
    public class ImportFormException(string fileName, string message) : Exception($"{fileName}: {message}")
    {
        public string FileName { get; } = fileName;
        /// <summary>Lý do, không kèm tên file</summary>
        public string Reason { get; } = message;
    }

    /// <summary>
    /// Đọc và biên dịch {verb}{Resource}.xml dạng &lt;import&gt; (KE_HOACH mục 2). Danh sách check/condition/tag/errorCode
    /// ngăn bằng ký tự CR: khai &lt;!ENTITY And "&amp;#38;#13;"&gt; (khai "&amp;#13;" thì CR bị chuẩn hóa thành dấu cách)
    /// </summary>
    public static partial class ImportFormLoader
    {
        public static readonly XNamespace Ns = "urn:schemas-fast-com:fast-api";
        private const char ListSeparator = '\r';

        /// <summary>Resource dành riêng cho route hệ thống</summary>
        private static readonly HashSet<string> ReservedResources = new(StringComparer.Ordinal) { "auth" };

        [GeneratedRegex("^([a-z]+)([A-Z][A-Za-z0-9]*)$")]
        private static partial Regex FileNamePattern();

        [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
        private static partial Regex KebabBoundary();

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
        private static partial Regex Identifier();

        public static ImportForm Load(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);

            XDocument document;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Parse,
                    // Entity SYSTEM (vd Irregular.txt) đọc theo đường dẫn tương đối với file form
                    XmlResolver = new XmlUrlResolver(),
                    MaxCharactersFromEntities = 1024 * 1024
                };
                using XmlReader reader = XmlReader.Create(Path.GetFullPath(filePath), settings);
                document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                throw new ImportFormException(name, $"XML không hợp lệ: {ex.Message}");
            }

            return Parse(name, document);
        }

        /// <summary>
        /// Tách tên file thành động từ (đoạn chữ thường đầu) và resource kebab-case: setSaleInvoice → set + sale-invoice
        /// </summary>
        public static bool TrySplitName(string name, out string verb, out string resource)
        {
            Match match = FileNamePattern().Match(name);
            if (!match.Success)
            {
                verb = resource = string.Empty;
                return false;
            }
            verb = match.Groups[1].Value;
            resource = KebabBoundary().Replace(match.Groups[2].Value, "-").ToLowerInvariant();
            return true;
        }

        /// <summary>set → POST /api/{resource}; động từ khác → POST /api/{resource}/{verb}</summary>
        public static string RouteOf(string verb, string resource) => verb == "set" ? $"/api/{resource}" : $"/api/{resource}/{verb}";

        /// <summary>Quyền đơn vị mặc định theo động từ (mục 3.2)</summary>
        public static string DefaultUnitAction(string verb) => verb switch
        {
            "get" => "Acc",
            "set" => "New",
            "delete" => "Del",
            _ => "Edit"
        };

        public static ImportForm Parse(string name, XDocument document)
        {
            if (!TrySplitName(name, out string verb, out string resource))
            {
                throw new ImportFormException(name, "tên file phải dạng {động từ chữ thường}{Resource}, vd setSaleInvoice");
            }
            if (ReservedResources.Contains(resource))
            {
                throw new ImportFormException(name, $"resource '{resource}' dành cho route hệ thống");
            }

            XElement root = document.Root ?? throw new ImportFormException(name, "file rỗng");
            if (root.Name != Ns + "import")
            {
                throw new ImportFormException(name, $"phần tử gốc phải là <import xmlns=\"{Ns}\">");
            }

            List<string> warnings = [];

            ImportFormType type = ParseEnum<ImportFormType>(name, root, "type");
            string unitAction = Attr(root, "unitAction") is { Length: > 0 } action ? action : DefaultUnitAction(verb);
            if (unitAction is not ("Acc" or "New" or "Edit" or "Del"))
            {
                throw new ImportFormException(name, $"unitAction '{unitAction}' phải là Acc/New/Edit/Del");
            }

            List<ImportBlock> blocks = ParseBlocks(name, root);

            Dictionary<string, string> queries = [];
            foreach (XElement query in root.Elements(Ns + "query"))
            {
                string queryAction = Attr(query, "action") is { Length: > 0 } a ? a.ToLowerInvariant() : "processing";
                if (queryAction is not ("checking" or "processing" or "transferred"))
                {
                    throw new ImportFormException(name, $"query action '{queryAction}' phải là checking, processing hoặc transferred");
                }
                if (!queries.TryAdd(queryAction, query.Element(Ns + "text")?.Value ?? query.Value))
                {
                    throw new ImportFormException(name, $"khai trùng query {queryAction}");
                }
            }
            if (!queries.ContainsKey("processing"))
            {
                throw new ImportFormException(name, "thiếu query processing");
            }

            string checking = CheckingCompiler.Expand(queries.GetValueOrDefault("checking", string.Empty), blocks, warnings);

            ImportPaging? paging = null;
            if (root.Element(Ns + "paging") is XElement pagingElement)
            {
                int pageSize = ParseInt(name, pagingElement, "pageSize") ?? throw new ImportFormException(name, "paging thiếu pageSize");
                string pagingKey = Attr(pagingElement, "key");
                if (pageSize <= 0 || !Identifier().IsMatch(pagingKey))
                {
                    throw new ImportFormException(name, "paging cần pageSize > 0 và key là tên cột hợp lệ");
                }
                paging = new ImportPaging(pageSize, pagingKey);
            }

            // Transfer đánh dấu theo key của trang đã trả: cần paging (key) và query transferred
            bool hasTransferred = queries.ContainsKey("transferred");
            if (type == ImportFormType.Transfer && (paging == null || !hasTransferred))
            {
                throw new ImportFormException(name, "form Transfer phải khai <paging key=…> và <query action=\"transferred\">");
            }
            if (type != ImportFormType.Transfer && hasTransferred)
            {
                throw new ImportFormException(name, "query transferred chỉ dùng cho form type=\"Transfer\"");
            }

            return new ImportForm
            {
                Name = name,
                Verb = verb,
                Resource = resource,
                Route = RouteOf(verb, resource),
                Type = type,
                VoucherCode = Attr(root, "voucherCode"),
                UnitRequired = ParseBool(name, root, "unitRequired", false),
                UnitAction = unitAction,
                Enable = ParseBool(name, root, "enable", true),
                Description = Attr(root, "description"),
                Blocks = blocks,
                CheckingQuery = checking,
                ProcessingQuery = queries["processing"],
                TransferredQuery = queries.GetValueOrDefault("transferred", string.Empty),
                Paging = paging,
                Warnings = warnings
            };
        }

        private static List<ImportBlock> ParseBlocks(string name, XElement root)
        {
            List<XElement> elements = root.Elements(Ns + "fields").ToList();
            if (elements.Count == 0)
            {
                throw new ImportFormException(name, "cần ít nhất một khối <fields>");
            }

            List<ImportBlock> blocks = [];
            HashSet<string> blockNames = new(StringComparer.OrdinalIgnoreCase);
            ImportField? rootKey = null;

            for (int i = 0; i < elements.Count; i++)
            {
                XElement element = elements[i];
                bool isRoot = i == 0;
                string blockName = Attr(element, "name");
                if (!Identifier().IsMatch(blockName) || !blockNames.Add(blockName))
                {
                    throw new ImportFormException(name, $"tên khối '{blockName}' trống, sai dạng hoặc trùng");
                }

                string key = Attr(element, "key");
                if (!isRoot && key.Length > 0)
                {
                    throw new ImportFormException(name, $"khối con '{blockName}' không được khai key (chỉ khối gốc)");
                }

                List<ImportField> fields = element.Elements(Ns + "field").Select(f => ParseField(name, blockName, f)).ToList();
                if (fields.Count == 0)
                {
                    throw new ImportFormException(name, $"khối '{blockName}' không có field");
                }
                EnsureUnique(name, blockName, fields);

                if (isRoot)
                {
                    if (key.Length > 0)
                    {
                        rootKey = fields.FirstOrDefault(f => f.Name == key)
                            ?? throw new ImportFormException(name, $"key '{key}' không phải field của khối gốc '{blockName}'");
                    }
                    else if (elements.Count > 1)
                    {
                        throw new ImportFormException(name, $"có khối con thì khối gốc '{blockName}' phải khai key");
                    }
                }
                else if (rootKey != null)
                {
                    // Khóa khối gốc tự thêm vào khối con để query nối #gốc với #con
                    ImportField? declared = fields.FirstOrDefault(f => f.Name == rootKey.Name);
                    if (declared == null)
                    {
                        fields.Insert(0, new ImportField
                        {
                            Name = rootKey.Name,
                            FastName = rootKey.FastName,
                            Type = rootKey.Type,
                            MaxLength = rootKey.MaxLength,
                            Format = rootKey.Format,
                            AllowNull = true,
                            IsInheritedKey = true
                        });
                        EnsureUnique(name, blockName, fields);
                    }
                    else if (declared.FastName != rootKey.FastName)
                    {
                        throw new ImportFormException(name, $"field khóa '{rootKey.Name}' ở khối '{blockName}' phải cùng fastName '{rootKey.FastName}'");
                    }
                }

                blocks.Add(new ImportBlock { Name = blockName, Table = Attr(element, "table"), IsRoot = isRoot, Key = key, Fields = fields });
            }

            return blocks;
        }

        private static ImportField ParseField(string name, string blockName, XElement element)
        {
            string fieldName = Attr(element, "name");
            string fastName = Attr(element, "fastName");
            if (!Identifier().IsMatch(fieldName))
            {
                throw new ImportFormException(name, $"khối '{blockName}': tên field '{fieldName}' trống hoặc sai dạng");
            }
            if (!Identifier().IsMatch(fastName))
            {
                throw new ImportFormException(name, $"{blockName}.{fieldName}: fastName '{fastName}' trống hoặc sai dạng");
            }

            int? maxLength = ParseInt(name, element, "maxLength");
            if (maxLength is <= 0)
            {
                throw new ImportFormException(name, $"{blockName}.{fieldName}: maxLength phải > 0");
            }

            return new ImportField
            {
                Name = fieldName,
                FastName = fastName,
                Type = ParseFieldType(name, blockName, fieldName, Attr(element, "type")),
                MaxLength = maxLength,
                AllowNull = ParseBool(name, element, "allowNull", true),
                IsPrimaryKey = ParseBool(name, element, "isPrimaryKey", false),
                UpperCase = ParseBool(name, element, "upperCase", false),
                Hidden = ParseBool(name, element, "hidden", false),
                DefaultValue = element.Attribute("defaultValue")?.Value,
                JustContent = ParseBool(name, element, "justContent", false),
                CheckIrregular = ParseBool(name, element, "checkIrregular", false),
                Format = Attr(element, "format"),
                Description = Attr(element, "description"),
                Example = Attr(element, "example"),
                Category = ParseInt(name, element, "category"),
                Checks = ParseChecks(name, $"{blockName}.{fieldName}", element)
            };
        }

        /// <summary>
        /// 4 danh sách song song: check bắt buộc có errorCode cùng vị trí; condition/tag không khai thì rỗng.
        /// Số phần tử lệch nhau → lỗi nạp
        /// </summary>
        private static List<ImportCheck> ParseChecks(string name, string field, XElement element)
        {
            string check = element.Attribute("check")?.Value ?? string.Empty;
            if (check.Length == 0)
            {
                foreach (string other in new[] { "condition", "tag", "errorCode" })
                {
                    if (element.Attribute(other) != null)
                    {
                        throw new ImportFormException(name, $"{field}: có {other} nhưng không có check");
                    }
                }
                return [];
            }

            string[] checks = check.Split(ListSeparator);
            string[] errorCodes = (element.Attribute("errorCode")?.Value ?? string.Empty).Split(ListSeparator);
            string[] conditions = SplitOptional(element, "condition", checks.Length);
            string[] tags = SplitOptional(element, "tag", checks.Length);

            if (errorCodes.Length != checks.Length || conditions.Length != checks.Length || tags.Length != checks.Length)
            {
                throw new ImportFormException(name,
                    $"{field}: check ({checks.Length}), condition ({conditions.Length}), tag ({tags.Length}), errorCode ({errorCodes.Length}) phải cùng số phần tử, ngăn bằng &And;");
            }

            List<ImportCheck> result = [];
            for (int i = 0; i < checks.Length; i++)
            {
                if (checks[i].Trim().Length == 0 || errorCodes[i].Trim().Length == 0)
                {
                    throw new ImportFormException(name, $"{field}: check và errorCode thứ {i + 1} không được trống");
                }
                result.Add(new ImportCheck(checks[i].Trim(), conditions[i].Trim(), tags[i].Trim(), errorCodes[i].Trim()));
            }
            return result;
        }

        private static string[] SplitOptional(XElement element, string attribute, int count) =>
            element.Attribute(attribute)?.Value.Split(ListSeparator) ?? Enumerable.Repeat(string.Empty, count).ToArray();

        private static void EnsureUnique(string name, string blockName, List<ImportField> fields)
        {
            string? duplicate = fields.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key
                ?? fields.GroupBy(f => f.FastName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
            if (duplicate != null)
            {
                throw new ImportFormException(name, $"khối '{blockName}' trùng field '{duplicate}'");
            }
        }

        private static ImportFieldType ParseFieldType(string name, string blockName, string fieldName, string value) => value.ToLowerInvariant() switch
        {
            "" or "string" => ImportFieldType.String,
            "numeric" => ImportFieldType.Numeric,
            "datetime" => ImportFieldType.DateTime,
            "bool" => ImportFieldType.Bool,
            "int" => ImportFieldType.Int,
            "unixtime" => ImportFieldType.UnixTime,
            _ => throw new ImportFormException(name, $"{blockName}.{fieldName}: type '{value}' không hỗ trợ (string, numeric, datetime, bool, int, unixtime)")
        };

        private static T ParseEnum<T>(string name, XElement element, string attribute) where T : struct, Enum
        {
            string value = Attr(element, attribute);
            if (Enum.TryParse(value, ignoreCase: true, out T result) && !int.TryParse(value, out _))
            {
                return result;
            }
            throw new ImportFormException(name, $"{attribute} '{value}' phải là {string.Join("/", Enum.GetNames<T>())}");
        }

        private static bool ParseBool(string name, XElement element, string attribute, bool defaultValue)
        {
            string value = Attr(element, attribute).ToLowerInvariant();
            return value switch
            {
                "" => defaultValue,
                "true" or "1" => true,
                "false" or "0" => false,
                _ => throw new ImportFormException(name, $"{attribute}='{value}' phải là true/false")
            };
        }

        private static int? ParseInt(string name, XElement element, string attribute)
        {
            string value = Attr(element, attribute);
            if (value.Length == 0)
            {
                return null;
            }
            return int.TryParse(value, out int result) ? result : throw new ImportFormException(name, $"{attribute}='{value}' phải là số nguyên");
        }

        private static string Attr(XElement element, string attribute) => element.Attribute(attribute)?.Value.Trim() ?? string.Empty;
    }
}
