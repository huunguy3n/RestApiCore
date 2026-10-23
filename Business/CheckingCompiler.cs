using System.Text;
using System.Text.RegularExpressions;
using Models;

namespace Business
{
    /// <summary>
    /// Biên dịch check của field thành SQL ghi #errors, thay vào @@checking / @@checking(n) trong query checking
    /// (KE_HOACH 2.4). Chạy một lần lúc nạp form
    /// </summary>
    public static partial class CheckingCompiler
    {
        /// <summary>Bảng lỗi engine tạo trước query checking</summary>
        public const string ErrorsTable = "CREATE TABLE #errors (block VARCHAR(64), row_no INT, parent_no INT, field VARCHAR(64), code VARCHAR(64))";

        [GeneratedRegex(@"@@checking(?:\((\d+)\))?", RegexOptions.IgnoreCase)]
        private static partial Regex CheckingToken();

        [GeneratedRegex(@"@@table\b", RegexOptions.IgnoreCase)]
        private static partial Regex TableToken();

        /// <summary>
        /// Thay @@checking(n) bằng check của field có category = n (theo giá trị category, không theo thứ tự),
        /// @@checking không số bằng check của mọi field
        /// </summary>
        public static string Expand(string query, IReadOnlyList<ImportBlock> blocks, List<string> warnings)
        {
            bool usesCategory = false;
            HashSet<int> usedCategories = [];

            string expanded = CheckingToken().Replace(query, match =>
            {
                if (!match.Groups[1].Success)
                {
                    return Compile(blocks, _ => true);
                }

                int category = int.Parse(match.Groups[1].Value);
                usesCategory = true;
                usedCategories.Add(category);
                return Compile(blocks, field => field.Category == category);
            });

            if (usesCategory)
            {
                foreach (ImportBlock block in blocks)
                {
                    foreach (ImportField field in block.Fields.Where(f => f.Checks.Count > 0))
                    {
                        if (field.Category == null)
                        {
                            warnings.Add($"{block.Name}.{field.Name} có check nhưng không khai category, @@checking(n) sẽ không chạy");
                        }
                        else if (!usedCategories.Contains(field.Category.Value))
                        {
                            warnings.Add($"{block.Name}.{field.Name} có category={field.Category} nhưng query checking không gọi @@checking({field.Category})");
                        }
                    }
                }
            }

            return expanded;
        }

        private static string Compile(IReadOnlyList<ImportBlock> blocks, Func<ImportField, bool> select)
        {
            StringBuilder sql = new();
            foreach (ImportBlock block in blocks)
            {
                foreach (ImportField field in block.Fields.Where(select))
                {
                    foreach (ImportCheck check in field.Checks)
                    {
                        AppendCheck(sql, block, field, check);
                    }
                }
            }
            return sql.ToString();
        }

        private static void AppendCheck(StringBuilder sql, ImportBlock block, ImportField field, ImportCheck check)
        {
            string table = "#" + block.Name;
            string clause = TableToken().Replace(check.Check, table);

            if (check.Condition.Length > 0)
            {
                sql.AppendLine(TableToken().Replace(check.Condition, table));
            }

            sql.Append("INSERT INTO #errors (block, row_no, parent_no, field, code) SELECT ")
                .Append(Literal(block.Name)).Append(", _row, _parent, ")
                .Append(Literal(field.Name)).Append(", ")
                .Append(Literal(check.ErrorCode))
                .Append(" FROM ").Append(table)
                .Append(" WHERE ").Append(NotEmpty(field))
                .Append(" AND (").Append(clause).AppendLine(")");

            if (check.Tag.Length > 0)
            {
                sql.AppendLine(TableToken().Replace(check.Tag, table));
            }
        }

        /// <summary>
        /// Như Upload: giá trị rỗng không kiểm (bắt buộc nhập do allowNull lo)
        /// </summary>
        private static string NotEmpty(ImportField field) => field.Type == ImportFieldType.String
            ? $"ISNULL({field.FastName}, '') <> ''"
            : $"{field.FastName} IS NOT NULL";

        private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
