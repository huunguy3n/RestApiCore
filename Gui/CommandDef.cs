using Cli;

namespace Gui
{
    public enum ParamKind { Text, Password, File, Folder, FileOrFolder, Flag }

    /// <summary>
    /// Một tham số của lệnh. ArgName null = tham số vị trí (đứng đầu dòng lệnh).
    /// Password không bao giờ đi qua dòng lệnh: GUI nạp vào biến môi trường tạm rồi truyền tên biến.
    /// </summary>
    public sealed record ParamDef(
        string Label,
        string? ArgName,
        ParamKind Kind,
        string Help,
        bool Required = false,
        string Default = "",
        string Filter = "Tất cả|*.*",
        bool IsApply = false);

    public sealed record CommandDef(
        string Name,
        string Title,
        string Description,
        ParamDef[] Params,
        Func<string[], Task<int>> Run);

    /// <summary>
    /// Danh mục lệnh. Thêm tính năng mới cho CLI = thêm một CommandDef ở đây, form tự sinh.
    /// </summary>
    public static class CommandCatalog
    {
        private const string JsonFilter = "JSON|*.json|Tất cả|*.*";

        private static ParamDef Config() => new("File cấu hình", "--config", ParamKind.File,
            "appsettings.Local.json chứa connection string và khóa", true, Filter: JsonFilter);

        private static ParamDef Apply() => new("Áp dụng (ghi thật)", "--apply", ParamKind.Flag,
            "Bỏ chọn = chỉ kiểm thử, không ghi database", IsApply: true);

        public static readonly CommandDef[] All =
        [
            new("create-user", "Tạo người dùng", "Tạo restApiUser: mã hóa mật khẩu portal, nối name_erp → vsysuserinfo, cấp form và private_key tuỳ chọn.",
            [
                Config(),
                new("Tên user", "--user", ParamKind.Text, "restApiUser.name, tối đa 32 ký tự", true),
                new("Tên NSD FBO", "--name-erp", ParamKind.Text, "userinfo2.name, tối đa 30 ký tự, khớp NSD trên FBO", true),
                new("Mật khẩu portal", "--password-env", ParamKind.Password, "Không lộ trên dòng lệnh; GUI truyền qua biến môi trường tạm", true),
                new("Form được phép", "--forms", ParamKind.Text, "Tên file XML không đuôi, ngăn cách bằng dấu phẩy"),
                new("Cấp private_key", "--issue-key", ParamKind.Flag, "Kèm khóa HMAC, chỉ hiện đúng một lần"),
                Apply()
            ], CreateUserCommand.RunAsync),

            new("set-password", "Đổi mật khẩu", "Đặt hoặc đổi mật khẩu portal (AES passwordKey).",
            [
                Config(),
                new("Tên user", "--user", ParamKind.Text, "restApiUser.name", true),
                new("Mật khẩu mới", "--password-env", ParamKind.Password, "Truyền qua biến môi trường tạm", true),
                Apply()
            ], SetPasswordCommand.RunAsync),

            new("issue-key", "Cấp private key", "Cấp private_key (HMAC-SHA256) cho tài khoản, lưu mã hóa bằng Security:KeyEncryptionKey. Khóa in ra đúng một lần khi áp dụng.",
            [
                Config(),
                new("Tên user", "--user", ParamKind.Text, "restApiUser.name", true),
                new("Xoay khóa (rotate)", "--rotate", ParamKind.Flag, "Bắt buộc nếu tài khoản đã có khóa"),
                Apply()
            ], IssueKeyCommand.RunAsync),

            new("rekey-passwords", "Đổi khóa mật khẩu", "Mã hóa lại restApiUser.password từ khóa cũ sang khóa mới, trong một transaction.",
            [
                Config(),
                new("Khóa cũ", "--old-key", ParamKind.Text, "Mặc định appSettings:secretKey"),
                new("Khóa mới", "--new-key", ParamKind.Text, "Mặc định appSettings:passwordKey"),
                Apply()
            ], RekeyPasswordsCommand.RunAsync),

            new("upgrade", "Nâng cấp form v2", "Chuyển form FastAPI 2.4.7 (v2) sang <import> v3, giữ tên file. Không chọn thư mục xuất thì chỉ in ra.",
            [
                new("File / thư mục form v2", null, ParamKind.FileOrFolder, "File .xml hoặc thư mục App_Data\\FastAPI", true, Filter: "XML|*.xml|Tất cả|*.*"),
                new("File cấu hình", "--config", ParamKind.File, "Chỉ đọc DB để tách master/detail chứng từ theo cột thật; bỏ trống thì không tách", Filter: JsonFilter),
                new("Thư mục xuất", "--out", ParamKind.Folder, "Bỏ trống = chỉ in ra màn hình"),
                new("Ghi đè file có sẵn", "--force", ParamKind.Flag, "")
            ], args => Task.FromResult(UpgradeCommand.Run(args)))
        ];
    }
}
