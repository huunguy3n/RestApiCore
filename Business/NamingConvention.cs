using System.Text.RegularExpressions;

namespace Business
{
    /// <summary>
    /// Quy ước đặt tên khai trong &lt;import jsonNaming="camelCase" columnNaming="snake_case"&gt;: tên JSON (khối, field, thuộc tính
    /// response) là camelCase, tên cột bảng FBO / bảng tạm (fastName) là snake_case. Cột query processing trả về dùng làm JSON response nên cũng phải camelCase (không tự đổi: sai thì từ chối)
    /// </summary>
    public static partial class NamingConvention
    {
        public const string CamelCase = "camelCase";
        public const string SnakeCase = "snake_case";

        [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
        private static partial Regex SnakePattern();

        [GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
        private static partial Regex CamelPattern();

        public static bool IsCamelCase(string name) => CamelPattern().IsMatch(name);

        public static bool IsSnakeCase(string name) => SnakePattern().IsMatch(name);
    }
}
