namespace Gui.Core
{
    public sealed record BuildResult(List<string> Args, Dictionary<ParamDef, string> Errors)
    {
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>Dựng mảng đối số và kiểm tra tham số; không phụ thuộc WinForms. Mật khẩu chỉ đi bằng tên biến môi trường.</summary>
    public static class CommandLine
    {
        public const string PasswordEnv = "FAST_API_GUI_PASSWORD";

        public static BuildResult Build(CommandDef command, Func<ParamDef, string> value)
        {
            var args = new List<string>();
            var errors = new Dictionary<ParamDef, string>();

            // Tham số vị trí đứng trước, giữ đúng thứ tự khi gõ tay
            foreach (ParamDef p in command.Params.OrderBy(p => p.ArgName == null ? 0 : 1))
            {
                string v = value(p);
                if (p.Kind == ParamKind.Flag)
                {
                    if (v.Length > 0) { args.Add(p.ArgName!); }
                    continue;
                }
                if (v.Length == 0)
                {
                    if (p.Required) { errors[p] = $"Vui lòng nhập {p.Label}"; }
                    continue;
                }

                string? problem = Check(p, v);
                if (problem != null) { errors[p] = problem; continue; }

                if (p.ArgName != null) { args.Add(p.ArgName); }
                args.Add(p.Kind == ParamKind.Password ? PasswordEnv : v);
            }
            return new BuildResult(args, errors);
        }

        private static string? Check(ParamDef p, string v) => p.Kind switch
        {
            ParamKind.File when !File.Exists(v) => "File không tồn tại",
            ParamKind.Folder when !Directory.Exists(v) => "Thư mục không tồn tại",
            ParamKind.FileOrFolder when !File.Exists(v) && !Directory.Exists(v) => "File hoặc thư mục không tồn tại",
            _ => null
        };

        /// <summary>Chỉ để hiển thị/sao chép. Việc chạy dùng mảng đối số, không đi qua shell.</summary>
        public static string Format(string commandName, IEnumerable<string> args) =>
            "Fast.Api.Cli " + commandName + string.Concat(args.Select(a => " " + Quote(a)));

        private static string Quote(string a) =>
            a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
