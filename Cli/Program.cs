using Cli;

// Fast.Api.Cli — công cụ quản trị FastAPI v3
// Bí mật đọc từ file cấu hình và biến môi trường, không nhận qua tham số dòng lệnh (tránh lộ trong lịch sử shell)

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        Fast.Api.Cli create-user --config <appsettings.Local.json> --user <name> --name-erp <userinfo2.name>
                                 --password-env <ENV> [--forms form1,form2] [--issue-key] [--apply]

          Tạo restApiUser: mã hóa mật khẩu portal bằng passwordKey, nối name_erp → vsysuserinfo.
          Mật khẩu đọc từ biến môi trường (không truyền trên dòng lệnh). --forms = tên file XML không đuôi.
          --issue-key kèm private_key HMAC (in một lần). Mặc định kiểm thử; --apply mới ghi.

        Fast.Api.Cli set-password --config <appsettings.Local.json> --user <name> --password-env <ENV> [--apply]

          Đặt / đổi mật khẩu portal (AES passwordKey). Mật khẩu từ biến môi trường.

        Fast.Api.Cli rekey-passwords --config <appsettings.Local.json> [--apply]
                                     [--old-key <khóa cấu hình>] [--new-key <khóa cấu hình>]

          Mã hóa lại restApiUser.password từ khóa cũ sang khóa mới.
          Mặc định chỉ kiểm thử (không ghi). --apply mới ghi, trong một transaction.
          --old-key mặc định appSettings:secretKey, --new-key mặc định appSettings:passwordKey.
          Giá trị lấy từ file --config, biến môi trường (vd appSettings__passwordKey) ghi đè.

        Fast.Api.Cli issue-key --config <appsettings.Local.json> --user <restApiUser.name> [--rotate] [--apply]

          Cấp private_key (HMAC-SHA256 v3) cho tài khoản, lưu mã hóa bằng Security:KeyEncryptionKey.
          Khóa in ra đúng một lần khi --apply. Đã có khóa thì phải thêm --rotate.

        Fast.Api.Cli create-portal-account --config <json> --login <tên> --name <hiển thị> --kind staff|customer
                                           --password-env <ENV> [--role partner|deployment|administrator] [--project <mã>]
                                           [--project-user <restApiUser>] [--allow-weak-password] [--apply]

          Tạo tài khoản đăng nhập portal trung tâm (DB portal riêng, ControlPlane:ConnectionString). Khách hàng: --kind customer
          --project <mã> (vai trò partner, --project-user là restApiUser của khách). Staff: deployment cần --project;
          administrator không --project là quyền mọi dự án. Mật khẩu (≥ 12 ký tự) từ biến môi trường, lưu băm PBKDF2;
          --allow-weak-password hạ mức tối thiểu xuống 6 ký tự cho môi trường thử (có cảnh báo và ghi vào audit).

        Fast.Api.Cli apply-package --package <gói.zip> --target <App_Data\Form> --manifest-sha256 <hex> --environment DEV|PROD
                                   --operator <tên> [--result <file.json>] [--apply]

          Áp gói form xuất từ portal lên thư mục form của môi trường. Mặc định chỉ kiểm thử (không ghi).
          --manifest-sha256: lấy ở màn hình Gói của portal (đối chiếu ngoài băng tần, sai là từ chối).
          Kiểm lại từng form bằng đúng bộ kiểm của portal và bắt trùng route; ghi từng file qua file tạm rồi thay nguyên tử,
          bản cũ sao lưu vào _backup. Luôn xuất file kết quả JSON để nhập vào màn hình Đồng bộ của portal.

        Fast.Api.Cli upgrade <file.xml | thư mục App_Data\FastAPI> [--config <appsettings.Local.json>] [--out <thư mục>] [--force]

          Chuyển form FastAPI 2.4.7 (v2: <import> phẳng, entity Include\*.txt) sang <import> v3, giữ tên file.
          Đổi tên field sang camelCase, justContent thành check, processing để lại làm chú thích (viết lại cho v3).
          --config (chỉ đọc DB) để tách master/detail chứng từ theo cột m??$000000 / d??$000000.
          In ghi chú cần xem lại; không có --out thì chỉ in ra.
        """);
    return 0;
}

try
{
    return args[0] switch
    {
        "create-user" => await CreateUserCommand.RunAsync(args[1..]),
        "set-password" => await SetPasswordCommand.RunAsync(args[1..]),
        "rekey-passwords" => await RekeyPasswordsCommand.RunAsync(args[1..]),
        "issue-key" => await IssueKeyCommand.RunAsync(args[1..]),
        "create-portal-account" => await CreatePortalAccountCommand.RunAsync(args[1..]),
        "apply-package" => ApplyPackageCommand.Run(args[1..]),
        "upgrade" => UpgradeCommand.Run(args[1..]),
        _ => throw new ArgumentException($"Lệnh không hỗ trợ: {args[0]}")
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
