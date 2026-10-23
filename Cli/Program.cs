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
        """);
    return 0;
}

try
{
    return args[0] switch
    {
        "rekey-passwords" => await RekeyPasswordsCommand.RunAsync(args[1..]),
        _ => throw new ArgumentException($"Lệnh không hỗ trợ: {args[0]}")
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
