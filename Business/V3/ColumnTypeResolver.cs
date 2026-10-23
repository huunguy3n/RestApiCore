using Models.V3;

namespace Business.V3
{
    /// <summary>
    /// Gắn kiểu cột bảng tạm cho từng field (KE_HOACH_V3 2.3): khối có table thì theo cột thật, không thì theo type XML.
    /// Chạy một lần lúc nạp form; maxLength lớn hơn cột thật thì cảnh báo và kiểm theo cột thật
    /// </summary>
    public static class ColumnTypeResolver
    {
        private static readonly HashSet<string> StringTypes = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar", "text", "ntext" };

        /// <param name="lookup">Cột của bảng theo tên; null = bảng không tồn tại</param>
        public static ImportForm Resolve(ImportForm form, Func<string, IReadOnlyDictionary<string, ColumnInfo>?> lookup)
        {
            List<string> warnings = [.. form.Warnings];
            List<ImportBlock> blocks = [];

            foreach (ImportBlock block in form.Blocks)
            {
                IReadOnlyDictionary<string, ColumnInfo>? columns = null;
                if (block.Table.Length > 0)
                {
                    columns = lookup(block.Table) ?? throw new ImportFormException(form.Name, $"khối '{block.Name}': bảng '{block.Table}' không tồn tại");
                }

                List<ImportField> fields = [];
                foreach (ImportField field in block.Fields)
                {
                    if (columns != null && columns.TryGetValue(field.FastName, out ColumnInfo? column))
                    {
                        fields.Add(FromColumn(block, field, column, warnings));
                    }
                    else
                    {
                        if (columns != null)
                        {
                            warnings.Add($"{block.Name}.{field.Name}: bảng {block.Table} không có cột {field.FastName}, dùng type XML");
                        }
                        fields.Add(field with { SqlType = FromXmlType(field), EffectiveMaxLength = field.Type == ImportFieldType.String ? field.MaxLength : null });
                    }
                }
                blocks.Add(block with { Fields = fields });
            }

            return form with { Blocks = blocks, Warnings = warnings };
        }

        /// <summary>Kiểu khi khối không có table (bảng 2.3, cột phải)</summary>
        public static string FromXmlType(ImportField field) => field.Type switch
        {
            ImportFieldType.String => (field.MaxLength is int length ? $"NVARCHAR({length})" : "NVARCHAR(MAX)") + " COLLATE DATABASE_DEFAULT",
            ImportFieldType.Numeric => "NUMERIC(30,12)",
            ImportFieldType.Int => "INT",
            ImportFieldType.Bool => "BIT",
            ImportFieldType.DateTime or ImportFieldType.UnixTime => "DATETIME",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        /// <summary>
        /// Kiểu SQL đúng như cột thật. Cột chuỗi thêm COLLATE DATABASE_DEFAULT để so được với bảng thật khi tempdb khác collation
        /// </summary>
        public static string FromColumn(ColumnInfo column)
        {
            string type = column.TypeName.ToLowerInvariant();
            string sql = type switch
            {
                "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
                    $"{type.ToUpperInvariant()}({(column.MaxLength < 0 ? "MAX" : column.MaxLength.ToString())})",
                "decimal" or "numeric" => $"{type.ToUpperInvariant()}({column.Precision},{column.Scale})",
                "datetime2" or "time" or "datetimeoffset" => $"{type.ToUpperInvariant()}({column.Scale})",
                _ => type.ToUpperInvariant()
            };
            return StringTypes.Contains(type) ? sql + " COLLATE DATABASE_DEFAULT" : sql;
        }

        private static ImportField FromColumn(ImportBlock block, ImportField field, ColumnInfo column, List<string> warnings)
        {
            int? effective = field.MaxLength;
            if (StringTypes.Contains(column.TypeName) && column.MaxLength > 0)
            {
                if (field.MaxLength > column.MaxLength)
                {
                    warnings.Add($"{block.Name}.{field.Name}: maxLength {field.MaxLength} lớn hơn cột {block.Table}.{column.Name} ({column.MaxLength}), kiểm theo cột thật");
                }
                effective = field.MaxLength is int length ? Math.Min(length, column.MaxLength) : column.MaxLength;
            }

            return field with { SqlType = FromColumn(column), EffectiveMaxLength = field.Type == ImportFieldType.String ? effective : null };
        }
    }
}
