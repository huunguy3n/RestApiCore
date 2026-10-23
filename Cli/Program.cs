using Cli;

// Fast.Api.Cli — công cụ quản trị FastAPI v3
// Bí mật đọc từ file cấu hình và biến môi trường, không nhận qua tham số dòng lệnh (tránh lộ trong lịch sử shell)

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        Fast.Api.Cli rekey-passwords --config <appsettings.Local.json> [--apply]
                                     [--old-key <khóa cấu hình>] [--new-key <khóa cấu hình>]

          Mã hóa lại restApiUser.password từ khóa cũ sang khóa mới.
          Mặc định chỉ kiểm thử (không ghi). --apply mới ghi, trong một transaction.
          --old-key mặc định appSettings:secretKey, --new-key mặc định appSettings:passwordKey.
          Giá trị lấy từ file --config, biến môi trường (vd appSettings__passwordKey) ghi đè.

        Fast.Api.Cli issue-key --config <appsettings.Local.json> --user <restApiUser.name> [--rotate] [--apply]

          Cấp private_key (HMAC-SHA256 v3) cho tài khoản, lưu mã hóa bằng Security:KeyEncryptionKey.
          Khóa in ra đúng một lần khi --apply. Đã có khóa thì phải thêm --rotate.

        Fast.Api.Cli upgrade <file.xml | thư mục Form> [--update <Update.xml>] [--out <thư mục>] [--force]

          Chuyển form v2 (<form>) sang <import> v3, giữ tên file. <reference> thành check tồn tại mã theo Update.xml
          (mặc định ../Options/Update.xml), bỏ @@update, đổi biến v2. In ghi chú cần xem lại; không có --out thì chỉ in ra.
        """);
    return 0;
}

try
{
    return args[0] switch
    {
        "rekey-passwords" => await RekeyPasswordsCommand.RunAsync(args[1..]),
        "issue-key" => await IssueKeyCommand.RunAsync(args[1..]),
        "upgrade" => UpgradeCommand.Run(args[1..]),
        _ => throw new ArgumentException($"Lệnh không hỗ trợ: {args[0]}")
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
