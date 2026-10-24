using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Security;
using System.Data;

namespace Cli
{
    /// <summary>
    /// Đặt / đổi mật khẩu portal của restApiUser (AES bằng passwordKey).
    /// Mật khẩu đọc từ biến môi trường (--password-env), không nhận trên dòng lệnh.
    /// </summary>
    public static class SetPasswordCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            string? configPath = null, userName = null, passwordEnv = null;
            bool apply = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": configPath = CliArgs.Next(args, ref i); break;
                    case "--user": userName = CliArgs.Next(args, ref i); break;
                    case "--password-env": passwordEnv = CliArgs.Next(args, ref i); break;
                    case "--apply": apply = true; break;
                    default: throw new ArgumentException($"Tham số không hỗ trợ: {args[i]}");
                }
            }

            if (configPath == null || !File.Exists(configPath))
            {
                throw new ArgumentException("Thiếu --config hoặc file không tồn tại");
            }
            if (string.IsNullOrWhiteSpace(userName))
            {
                throw new ArgumentException("Thiếu --user <restApiUser.name>");
            }
            if (string.IsNullOrWhiteSpace(passwordEnv))
            {
                throw new ArgumentException("Thiếu --password-env <tên biến môi trường chứa mật khẩu portal>");
            }

            string? password = Environment.GetEnvironmentVariable(passwordEnv);
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException($"Biến môi trường '{passwordEnv}' trống hoặc chưa set");
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .AddEnvironmentVariables()
                .Build();

            string connectionString = config.GetConnectionString("appConnectionString") ?? "";
            string passwordKey = config["appSettings:passwordKey"] ?? "";
            if (connectionString.Length == 0)
            {
                throw new ArgumentException("Thiếu connectionStrings:appConnectionString");
            }
            if (passwordKey.Length != 32)
            {
                throw new ArgumentException("appSettings:passwordKey phải đúng 32 ký tự");
            }

            string cipher = EncryptionHelper.EncryptString(password, passwordKey);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            int? uId = null;
            await using (var select = new SqlCommand("SELECT id FROM restApiUser WHERE [name] = @name", connection))
            {
                select.Parameters.AddWithValue("@name", userName);
                object? value = await select.ExecuteScalarAsync();
                if (value is int id)
                {
                    uId = id;
                }
            }
            if (uId == null)
            {
                Console.Error.WriteLine($"Không có tài khoản restApiUser '{userName}'.");
                return 1;
            }

            Console.WriteLine($"Sẽ đổi password portal cho '{userName}' (u_id {uId}).");
            if (!apply)
            {
                Console.WriteLine("Kiểm thử (chưa ghi). Thêm --apply để cập nhật.");
                return 0;
            }

            await using (var update = new SqlCommand(
                "UPDATE restApiUser SET [password] = @password WHERE id = @id", connection))
            {
                update.Parameters.Add("@password", SqlDbType.VarChar, 256).Value = cipher;
                update.Parameters.AddWithValue("@id", uId.Value);
                await update.ExecuteNonQueryAsync();
            }

            Console.WriteLine($"Đã cập nhật mật khẩu portal cho '{userName}'.");
            return 0;
        }
    }
}
