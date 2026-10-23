using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Models;

namespace Business
{
    /// <summary>Lỗi giá trị một field, vị trí dạng [0].detail[2].ItemID</summary>
    public sealed record FieldError(string Position, string Block, string Field, string Code);

    public sealed class MappedBody
    {
        /// <summary>Mỗi khối một bảng (tên = tên khối), cột _row, _parent rồi tới fastName</summary>
        public required DataSet Data { get; init; }
        public IReadOnlyList<FieldError> Errors { get; init; } = [];
        /// <summary>Khác null: body sai dạng (INVALID_SHAPE), Data rỗng</summary>
        public string? ShapeError { get; init; }
        public int DocumentCount { get; init; }
    }

    /// <summary>
    /// JSON object → bảng theo khối (KE_HOACH 4.3). Body là một chứng từ { "master": {…}, "detail": […] } hoặc mảng chứng từ.
    /// Form chỉ có một khối thì nhận thêm dạng phẳng { "Field": … } (body tham số của get). Thuộc tính không khai bị bỏ qua.
    /// Lỗi giá trị gom đủ, có vị trí; lỗi dạng dừng ngay
    /// </summary>
    public static partial class JsonBodyMapper
    {
        public const string RowColumn = "_row";
        public const string ParentColumn = "_parent";

        public const string FieldRequired = "FIELD_REQUIRED";
        public const string InvalidValue = "INVALID_VALUE";
        public const string ValueTooLong = "VALUE_TOO_LONG";
        public const string InvalidIrregular = "INVALID_FIELD_IRREGULAR";

        [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
        private static partial Regex IsoDatePrefix();

        /// <param name="irregular">Lớp ký tự đặc biệt (Irregular.txt) cho field checkIrregular; null = không kiểm</param>
        public static MappedBody Map(ImportForm form, byte[] body, int maxDocuments, Regex? irregular = null)
        {
            DataSet data = CreateDataSet(form);

            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(body.Length == 0 ? "{}"u8.ToArray() : body);
            }
            catch (JsonException ex)
            {
                return Shape(data, $"JSON không hợp lệ: {ex.Message}");
            }

            using (json)
            {
                JsonElement rootElement = json.RootElement;
                List<JsonElement> documents;
                if (rootElement.ValueKind == JsonValueKind.Array)
                {
                    documents = [.. rootElement.EnumerateArray()];
                    if (documents.Count == 0)
                    {
                        return Shape(data, "mảng chứng từ rỗng");
                    }
                }
                else if (rootElement.ValueKind == JsonValueKind.Object)
                {
                    documents = [rootElement];
                }
                else
                {
                    return Shape(data, "body phải là object hoặc mảng object");
                }

                if (documents.Count > maxDocuments)
                {
                    return Shape(data, $"tối đa {maxDocuments} chứng từ mỗi request");
                }

                List<FieldError> errors = [];
                for (int d = 0; d < documents.Count; d++)
                {
                    string? shape = MapDocument(form, documents[d], d, data, errors, irregular);
                    if (shape != null)
                    {
                        return Shape(CreateDataSet(form), $"[{d}]: {shape}");
                    }
                }

                return new MappedBody { Data = data, Errors = errors, DocumentCount = documents.Count };
            }
        }

        /// <summary>
        /// CREATE TABLE #khối theo SqlType đã gắn lúc nạp form
        /// </summary>
        public static string CreateTempTableSql(ImportBlock block) =>
            $"CREATE TABLE #{block.Name} ([{RowColumn}] INT NOT NULL, [{ParentColumn}] INT NOT NULL, "
            + string.Join(", ", block.Fields.Select(f => $"[{f.FastName}] {f.SqlType} NULL"))
            + ")";

        public static DataSet CreateDataSet(ImportForm form)
        {
            DataSet data = new();
            foreach (ImportBlock block in form.Blocks)
            {
                DataTable table = new(block.Name);
                table.Columns.Add(RowColumn, typeof(int));
                table.Columns.Add(ParentColumn, typeof(int));
                foreach (ImportField field in block.Fields)
                {
                    table.Columns.Add(field.FastName, ClrType(field.Type));
                }
                data.Tables.Add(table);
            }
            return data;
        }

        private static string? MapDocument(ImportForm form, JsonElement document, int d, DataSet data, List<FieldError> errors, Regex? irregular)
        {
            if (document.ValueKind != JsonValueKind.Object)
            {
                return "chứng từ phải là object";
            }

            ImportBlock root = form.Root;
            JsonElement rootObject;
            if (TryGetProperty(document, root.Name, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
            {
                rootObject = nested;
            }
            else if (form.Blocks.Count == 1)
            {
                // Form một khối: nhận cả dạng phẳng
                rootObject = document;
            }
            else
            {
                return $"thiếu khối gốc '{root.Name}' (object)";
            }

            DataRow rootRow = data.Tables[root.Name]!.NewRow();
            rootRow[RowColumn] = d;
            rootRow[ParentColumn] = d;
            FillRow(rootRow, root, rootObject, $"[{d}].{root.Name}", errors, irregular, inheritedKey: null);
            data.Tables[root.Name]!.Rows.Add(rootRow);

            ImportField? keyField = root.Key.Length > 0 ? root.Fields.First(f => f.Name == root.Key) : null;
            JsonElement? keyValue = keyField != null && TryGetProperty(rootObject, keyField.Name, out JsonElement value) ? value : null;

            foreach (ImportBlock block in form.Blocks.Skip(1))
            {
                if (!TryGetProperty(document, block.Name, out JsonElement rows) || rows.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                if (rows.ValueKind != JsonValueKind.Array)
                {
                    return $"khối con '{block.Name}' phải là mảng";
                }

                int i = 0;
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        return $"{block.Name}[{i}] phải là object";
                    }
                    // Dòng con gửi kèm khóa thì phải trùng khóa khối gốc
                    if (keyField != null && TryGetProperty(row, keyField.Name, out JsonElement childKey) && childKey.ValueKind != JsonValueKind.Null
                        && (keyValue == null || childKey.ToString() != keyValue.Value.ToString()))
                    {
                        return $"{block.Name}[{i}].{keyField.Name} khác khóa khối gốc";
                    }

                    DataRow dataRow = data.Tables[block.Name]!.NewRow();
                    dataRow[RowColumn] = i;
                    dataRow[ParentColumn] = d;
                    FillRow(dataRow, block, row, $"[{d}].{block.Name}[{i}]", errors, irregular, keyField != null ? rootRow[keyField.FastName] : null);
                    data.Tables[block.Name]!.Rows.Add(dataRow);
                    i++;
                }
            }

            return null;
        }

        private static void FillRow(DataRow row, ImportBlock block, JsonElement source, string position, List<FieldError> errors, Regex? irregular, object? inheritedKey)
        {
            foreach (ImportField field in block.Fields)
            {
                if (field.IsInheritedKey)
                {
                    row[field.FastName] = inheritedKey ?? DBNull.Value;
                    continue;
                }

                JsonElement? value = !field.Hidden && TryGetProperty(source, field.Name, out JsonElement element) ? element : null;
                string? code = Convert(field, value, irregular, out object result);
                if (code != null)
                {
                    errors.Add(new FieldError($"{position}.{field.Name}", block.Name, field.Name, code));
                }
                row[field.FastName] = result;
            }
        }

        /// <summary>
        /// Đổi giá trị JSON theo type XML. Trả mã lỗi hoặc null. Thiếu / null / chuỗi trắng thì dùng defaultValue, không có thì kiểm allowNull
        /// </summary>
        public static string? Convert(ImportField field, JsonElement? value, Regex? irregular, out object result)
        {
            result = DBNull.Value;
            bool missing = value == null || value.Value.ValueKind == JsonValueKind.Null
                || (value.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.Value.GetString()));

            if (missing)
            {
                if (field.DefaultValue != null)
                {
                    using JsonDocument fallback = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(field.DefaultValue));
                    return ConvertPresent(field, fallback.RootElement, irregular, out result);
                }
                return field.AllowNull || field.Hidden ? null : FieldRequired;
            }

            return ConvertPresent(field, value!.Value, irregular, out result);
        }

        private static string? ConvertPresent(ImportField field, JsonElement value, Regex? irregular, out object result)
        {
            result = DBNull.Value;
            switch (field.Type)
            {
                case ImportFieldType.String:
                    if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        return InvalidValue;
                    }
                    string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                    if (field.UpperCase)
                    {
                        text = text.ToUpperInvariant();
                    }
                    if (field.EffectiveMaxLength is int max && text.Length > max)
                    {
                        return ValueTooLong;
                    }
                    if (field.CheckIrregular && irregular != null && irregular.IsMatch(text))
                    {
                        return InvalidIrregular;
                    }
                    result = text;
                    return null;

                case ImportFieldType.Numeric:
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)
                        || value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
                    {
                        result = number;
                        return null;
                    }
                    return InvalidValue;

                case ImportFieldType.Int:
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int integer)
                        || value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                    {
                        result = integer;
                        return null;
                    }
                    return InvalidValue;

                case ImportFieldType.Bool:
                    bool? flag = value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => value.TryGetInt32(out int n) && n is 0 or 1 ? n == 1 : null,
                        JsonValueKind.String => value.GetString()!.Trim().ToLowerInvariant() switch
                        {
                            "true" or "1" => true,
                            "false" or "0" => false,
                            _ => null
                        },
                        _ => null
                    };
                    if (flag == null)
                    {
                        return InvalidValue;
                    }
                    result = flag.Value;
                    return null;

                case ImportFieldType.DateTime:
                    if (value.ValueKind == JsonValueKind.String && TryParseDate(value.GetString()!.Trim(), field.Format, out DateTime date))
                    {
                        result = date;
                        return null;
                    }
                    return InvalidValue;

                case ImportFieldType.UnixTime:
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seconds)
                        || value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                    {
                        try
                        {
                            result = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
                            return null;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            return InvalidValue;
                        }
                    }
                    return InvalidValue;
            }
            return InvalidValue;
        }

        /// <summary>
        /// Theo format của field (vd dd/MM/yyyy), luôn nhận thêm ISO 8601. Có múi giờ thì đổi về giờ máy chủ
        /// </summary>
        public static bool TryParseDate(string text, string format, out DateTime date)
        {
            if (format.Length > 0 && DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }
            if (IsoDatePrefix().IsMatch(text)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset offset))
            {
                date = HasOffset(text) ? offset.LocalDateTime : offset.DateTime;
                return true;
            }
            date = default;
            return false;
        }

        private static bool HasOffset(string text) => text.EndsWith('Z') || Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");

        private static Type ClrType(ImportFieldType type) => type switch
        {
            ImportFieldType.String => typeof(string),
            ImportFieldType.Numeric => typeof(decimal),
            ImportFieldType.Int => typeof(int),
            ImportFieldType.Bool => typeof(bool),
            _ => typeof(DateTime)
        };

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            // Tên thuộc tính không phân biệt hoa thường; trùng tên khác hoa thường thì lấy cái đầu
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static MappedBody Shape(DataSet data, string message) => new() { Data = data, ShapeError = message };
    }
}
