using System.Text.RegularExpressions;

namespace WebPortal.Portal
{
    /// <summary>
    /// Cấu hình WebPortal. Chuỗi kết nối (PortalDb, QLDA) là bí mật: khai ở appsettings.Local.json (gitignore) hoặc biến môi trường
    /// </summary>
    public sealed class PortalSettings
    {
        /// <summary>PortalDb (sql/portal/*)</summary>
        public string ConnectionString { get; set; } = "";

        /// <summary>Gốc được phép cho api_folder, vd \\172.168.5.14\CustomerPro\ — chống trỏ portal sang thư mục tùy ý</summary>
        public string[] AllowedApiRoots { get; set; } = [];

        public QldaSettings Qlda { get; set; } = new();

        public LicenseKeySettings License { get; set; } = new();
    }

    /// <summary>
    /// QLDA: bảng danh mục dự án (mặc định nbdmda). Tên bảng/cột cấu hình được vì chưa đối chiếu được với DB QLDA thật;
    /// chỉ nhận định danh SQL hợp lệ, được bọc [ ] khi ghép
    /// </summary>
    public sealed partial class QldaSettings
    {
        public string ConnectionString { get; set; } = "";
        public string Table { get; set; } = "nbdmda";
        public string CodeColumn { get; set; } = "ma_da";
        public string NameColumn { get; set; } = "ten_da";
        public string StatusColumn { get; set; } = "status";
        public string SourceDirColumn { get; set; } = "dir_src_web";
        public string ActiveStatus { get; set; } = "1";

        public bool IsValid => new[] { Table, CodeColumn, NameColumn, StatusColumn, SourceDirColumn }.All(n => Identifier().IsMatch(n));

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
        private static partial Regex Identifier();
    }

    /// <summary>Khóa riêng ký license: PFX ngoài thư mục release (KeyPath + KeyPassword) hoặc Certificate Store (KeyThumbprint)</summary>
    public sealed class LicenseKeySettings
    {
        public string KeyPath { get; set; } = "";
        public string KeyPassword { get; set; } = "";
        public string KeyThumbprint { get; set; } = "";
        /// <summary>Số ngày mặc định khi cấp license có hạn; 0 = không hạn</summary>
        public int DefaultValidDays { get; set; }
    }

    public static partial class ProjectCode
    {
        /// <summary>Đoạn URL dùng riêng của portal, không được làm mã dự án</summary>
        public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "cpanel", "login", "logout", "css", "js", "lib", "samples", "error", "favicon.ico", "health", "api", "session"
        };

        public static bool IsValid(string? code) => code != null && Pattern().IsMatch(code) && !Reserved.Contains(code);

        [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
        private static partial Regex Pattern();
    }

    public static partial class LoginName
    {
        /// <summary>Tên đăng nhập: chữ, số, . _ @ - (3-32 ký tự, vừa restApiUser.name)</summary>
        public static bool IsValid(string? login) => login != null && Pattern().IsMatch(login);

        public const int MinPasswordLength = 12;

        [GeneratedRegex("^[A-Za-z0-9._@-]{3,32}$")]
        private static partial Regex Pattern();
    }
}
