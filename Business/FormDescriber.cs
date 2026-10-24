using System.Globalization;
using Models;

namespace Business
{
    public sealed record FieldDescription(string Name, string Type, int? MaxLength, bool Required, string? Format, string? DefaultValue, bool UpperCase,
        string Description, string Example, IReadOnlyList<string> ErrorCodes);

    public sealed record BlockDescription(string Name, bool IsRoot, string? Key, IReadOnlyList<FieldDescription> Fields);

    /// <summary>
    /// Mô tả form cho người tích hợp (WebPortal, PortalApp): khối, field hiện được, mẫu body.
    /// Field hidden và khóa tự thêm ở khối con không hiện vì đối tác không gửi
    /// </summary>
    public static class FormDescriber
    {
        public static IEnumerable<ImportField> VisibleFields(ImportBlock block) => block.Fields.Where(f => !f.Hidden && !f.IsInheritedKey);

        public static IReadOnlyList<BlockDescription> Blocks(ImportForm form) => form.Blocks.Select(b => new BlockDescription(
            b.Name, b.IsRoot, b.Key.Length > 0 ? b.Key : null,
            VisibleFields(b).Select(f => new FieldDescription(
                f.Name,
                f.Type.ToString().ToLowerInvariant(),
                f.EffectiveMaxLength ?? f.MaxLength,
                !f.AllowNull,
                f.Type == ImportFieldType.DateTime ? (f.Format.Length > 0 ? f.Format + " hoặc ISO 8601" : "ISO 8601") : null,
                f.DefaultValue,
                f.UpperCase,
                f.Description,
                f.Example,
                f.Checks.Select(c => c.ErrorCode).Distinct().ToList())).ToList())).ToList();

        /// <summary>Mẫu body: form nhiều khối dạng { "gốc": { … }, "con": [ { … } ] }, form một khối dạng phẳng</summary>
        public static Dictionary<string, object?> Sample(ImportForm form)
        {
            Dictionary<string, object?> Row(ImportBlock block) => VisibleFields(block).ToDictionary(f => f.Name, SampleValue);

            if (form.Blocks.Count == 1)
            {
                return Row(form.Root);
            }
            Dictionary<string, object?> sample = new() { [form.Root.Name] = Row(form.Root) };
            foreach (ImportBlock block in form.Blocks.Skip(1))
            {
                sample[block.Name] = new[] { Row(block) };
            }
            return sample;
        }

        private static object? SampleValue(ImportField field)
        {
            if (field.Example.Length > 0)
            {
                return field.Type switch
                {
                    ImportFieldType.Numeric when decimal.TryParse(field.Example, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) => number,
                    ImportFieldType.Int when int.TryParse(field.Example, out int integer) => integer,
                    ImportFieldType.UnixTime when long.TryParse(field.Example, out long seconds) => seconds,
                    ImportFieldType.Bool when bool.TryParse(field.Example, out bool flag) => flag,
                    _ => field.Example
                };
            }
            return field.Type switch
            {
                ImportFieldType.Numeric => 0m,
                ImportFieldType.Int => 0,
                ImportFieldType.Bool => false,
                ImportFieldType.UnixTime => 1792574400L,
                ImportFieldType.DateTime => field.Format.Length > 0 ? new DateTime(2026, 10, 21).ToString(field.Format, CultureInfo.InvariantCulture) : "2026-10-21",
                _ => ""
            };
        }
    }
}
