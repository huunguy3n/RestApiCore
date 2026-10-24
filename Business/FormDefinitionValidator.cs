using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Models;

namespace Business
{
    public sealed record FormIssue(int Line, int Column, string Message);

    public sealed record FormValidation(IReadOnlyList<FormIssue> Errors, IReadOnlyList<FormIssue> Warnings, ImportForm? Form)
    {
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// Kiểm một định nghĩa form (XML) TRƯỚC khi lưu / publish / áp lên môi trường, dùng chung cho portal và CLI:
    /// tên file, kích thước, XML đúng cú pháp (cấm tham chiếu ngoài: không SYSTEM/PUBLIC entity, không DTD ngoài, giới hạn bung entity),
    /// luật nghiệp vụ của ImportFormLoader (khối, field, check, paging...), và rà SQL trong &lt;query&gt; để chặn các lệnh cấp máy chủ.
    /// Không thay thế việc duyệt của người publish: SQL động và hàm hệ thống chỉ cảnh báo, không chặn được hết
    /// </summary>
    public static partial class FormDefinitionValidator
    {
        public const int MaxBytes = 256 * 1024;
        public const int MaxNameLength = 32;   // restApiAllowForm.form varchar(32)

        [GeneratedRegex("^[a-z]+[A-Z][A-Za-z0-9]*$")]
        private static partial Regex NamePattern();

        [GeneratedRegex(@"<!ENTITY\s+(%\s*)?[^\s>]+\s+(SYSTEM|PUBLIC)\b", RegexOptions.IgnoreCase)]
        private static partial Regex ExternalEntity();

        [GeneratedRegex(@"--[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline)]
        private static partial Regex SqlComment();

        /// <summary>Lệnh cấp máy chủ / phá hoại không bao giờ thuộc về một form nghiệp vụ</summary>
        private static readonly (Regex Pattern, string Message)[] Forbidden =
        [
            (new Regex(@"\bxp_\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không dùng thủ tục mở rộng xp_*"),
            (new Regex(@"\bsp_OA\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không dùng sp_OA* (OLE Automation)"),
            (new Regex(@"\b(sp_configure|reconfigure|sp_addlogin|sp_addsrvrolemember|sp_password|sp_addlinkedserver)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không đổi cấu hình hoặc tài khoản máy chủ"),
            (new Regex(@"\b(openrowset|opendatasource|openquery)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không truy cập nguồn dữ liệu ngoài (OPENROWSET/OPENDATASOURCE/OPENQUERY)"),
            (new Regex(@"\bbulk\s+insert\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không dùng BULK INSERT"),
            (new Regex(@"\b(shutdown|dbcc|waitfor)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không dùng SHUTDOWN / DBCC / WAITFOR"),
            (new Regex(@"\b(grant|revoke|deny)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không đổi quyền (GRANT/REVOKE/DENY)"),
            (new Regex(@"\b(create|alter|drop)\s+(login|user|database|server|schema|role|trigger|procedure|proc|function|view)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không tạo/sửa/xóa đối tượng CSDL trong form"),
            (new Regex(@"\b(drop|truncate)\s+table\s+(?!#)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "chỉ được DROP/TRUNCATE bảng tạm (#...)"),
            (new Regex(@"\balter\s+table\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "không ALTER TABLE trong form")
        ];

        private static readonly Regex DynamicSql = new(@"\bexec(ute)?\s*\(|\bsp_executesql\b|\bexec(ute)?\s+@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsValidName(string? name) =>
            name is { Length: > 0 and <= MaxNameLength } && NamePattern().IsMatch(name) && ImportFormLoader.TrySplitName(name, out _, out _);

        public static FormValidation Validate(string? name, string? xml)
        {
            List<FormIssue> errors = [], warnings = [];

            if (!IsValidName(name))
            {
                errors.Add(new FormIssue(0, 0, $"Tên form '{name}' không hợp lệ: dạng {{động từ chữ thường}}{{Resource}} (vd setSaleInvoice), tối đa {MaxNameLength} ký tự"));
                return new FormValidation(errors, warnings, null);
            }
            if (string.IsNullOrWhiteSpace(xml))
            {
                errors.Add(new FormIssue(0, 0, "Nội dung form rỗng"));
                return new FormValidation(errors, warnings, null);
            }
            if (Encoding.UTF8.GetByteCount(xml) > MaxBytes)
            {
                errors.Add(new FormIssue(0, 0, $"Form vượt {MaxBytes / 1024} KB"));
                return new FormValidation(errors, warnings, null);
            }

            XDocument document;
            try
            {
                XmlReaderSettings settings = new()
                {
                    // Cần Parse để dùng entity nội bộ (vd And); XmlResolver = null nghĩa là không đọc được bất kỳ tài nguyên ngoài nào (XXE)
                    DtdProcessing = DtdProcessing.Parse,
                    XmlResolver = null,
                    MaxCharactersFromEntities = 64 * 1024,
                    MaxCharactersInDocument = MaxBytes
                };
                using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);
                document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                errors.Add(new FormIssue(ex.LineNumber, ex.LinePosition, $"XML không hợp lệ: {ex.Message}"));
                return new FormValidation(errors, warnings, null);
            }

            if (document.DocumentType is { } doctype)
            {
                if (!string.IsNullOrEmpty(doctype.SystemId) || !string.IsNullOrEmpty(doctype.PublicId) || ExternalEntity().IsMatch(doctype.InternalSubset ?? ""))
                {
                    errors.Add(new FormIssue(0, 0, "Không cho phép DTD/entity ngoài (SYSTEM/PUBLIC); form phải tự chứa đủ"));
                    return new FormValidation(errors, warnings, null);
                }
            }

            ImportForm? form = null;
            try
            {
                form = ImportFormLoader.Parse(name!, document);
                warnings.AddRange(form.Warnings.Select(w => new FormIssue(0, 0, w)));
            }
            catch (ImportFormException ex)
            {
                errors.Add(new FormIssue(0, 0, ex.Reason));
            }

            LintSql(document, errors, warnings);
            return new FormValidation(errors, warnings, errors.Count == 0 ? form : null);
        }

        private static void LintSql(XDocument document, List<FormIssue> errors, List<FormIssue> warnings)
        {
            foreach (XElement query in document.Descendants(ImportFormLoader.Ns + "query"))
            {
                int line = (query as IXmlLineInfo)?.HasLineInfo() == true ? ((IXmlLineInfo)query).LineNumber : 0;
                string sql = SqlComment().Replace(query.Value, " ");
                foreach (var (pattern, message) in Forbidden)
                {
                    if (pattern.IsMatch(sql))
                    {
                        errors.Add(new FormIssue(line, 0, $"SQL bị chặn: {message}"));
                    }
                }
                if (DynamicSql.IsMatch(sql))
                {
                    warnings.Add(new FormIssue(line, 0, "SQL động (EXEC(...) / sp_executesql): người publish phải đọc kỹ"));
                }
            }
        }

        /// <summary>Chuẩn hóa xuống dòng về LF để băm và so sánh ổn định giữa trình duyệt, CLI và máy chủ</summary>
        public static string Normalize(string xml) => xml.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');

        public static string Sha256(string normalizedXml) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalizedXml)));
    }
}
