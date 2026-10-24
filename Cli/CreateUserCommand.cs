using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Security;
using System.Data;

namespace Cli
{
    /// <summary>
    /// Tạo restApiUser mới: mã hóa password bằng passwordKey, nối name_erp → userinfo2, tuỳ chọn cấp form và private_key.
    /// Mật khẩu đọc từ biến môi trường (--password-env), không nhận trên dòng lệnh.
    /// </summary>
    public static class CreateUserCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            string? configPath = null, userName = null, nameErp = null, passwordEnv = null, formsCsv = null;
            bool apply = false, issueKey = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": configPath = CliArgs.Next(args, ref i); break;
                    case "--user": userName = CliArgs.Next(args, ref i); break;
                    case "--name-erp": nameErp = CliArgs.Next(args, ref i); break;
                    case "--password-env": passwordEnv = CliArgs.Next(args, ref i); break;
                    case "--forms": formsCsv = CliArgs.Next(args, ref i); break;
                    case "--issue-key": issueKey = true; break;
                    case "--apply": apply = true; break;
                    default: throw new ArgumentException($"Tham số không hỗ trợ: {args[i]}");
                }
            }

            if (configPath == null || !File.Exists(configPath))
            {
                throw new ArgumentException("Thiếu --config hoặc file không tồn tại");
            }
            if (string.IsNullOrWhiteSpace(userName) || userName.Length > 32)
            {
                throw new ArgumentException("Thiếu --user <restApiUser.name> (tối đa 32 ký tự)");
            }
            if (string.IsNullOrWhiteSpace(nameErp) || nameErp.Length > 30)
            {
                throw new ArgumentException("Thiếu --name-erp <userinfo2.name> (tối đa 30 ký tự, khớp NSD FBO)");
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

            string[] forms = (formsCsv ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

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

            PrivateKeyProtector? protector = null;
            if (issueKey)
            {
                protector = new PrivateKeyProtector(config["Security:KeyEncryptionKey"] ?? "");
            }

            string cipher = EncryptionHelper.EncryptString(password, passwordKey);
            if (EncryptionHelper.DecryptString(cipher, passwordKey) != password)
            {
                throw new InvalidOperationException("Mã hóa mật khẩu thất bại (kiểm tra passwordKey)");
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // name_erp phải khớp user FBO còn hiệu lực
            await using (var checkErp = new SqlCommand("""
                SELECT TOP 1 id, admin, status FROM dbo.vsysuserinfo WHERE RTRIM([name]) = RTRIM(@name_erp)
                """, connection))
            {
                checkErp.Parameters.AddWithValue("@name_erp", nameErp);
                await using var reader = await checkErp.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    Console.Error.WriteLine($"Không có user FBO name_erp='{nameErp}' trong vsysuserinfo. Tạo NSD trên FBO trước, hoặc sửa --name-erp.");
                    return 1;
                }
                string erpStatus = reader["status"]?.ToString()?.Trim() ?? "";
                bool admin = Convert.ToInt32(reader["admin"]) == 1;
                int userId = Convert.ToInt32(reader["id"]);
                if (erpStatus != "1")
                {
                    Console.Error.WriteLine($"User FBO '{nameErp}' (user_id {userId}) status='{erpStatus}', cần status='1'.");
                    return 1;
                }
                Console.WriteLine($"FBO: name_erp={nameErp}, user_id={userId}, admin={(admin ? 1 : 0)}");
            }

            await using (var exists = new SqlCommand("SELECT COUNT(1) FROM restApiUser WHERE [name] = @name", connection))
            {
                exists.Parameters.AddWithValue("@name", userName);
                if ((int)(await exists.ExecuteScalarAsync())! > 0)
                {
                    Console.Error.WriteLine($"Đã có restApiUser '{userName}'. Đổi mật khẩu: set-password. Cấp khóa: issue-key.");
                    return 1;
                }
            }

            Console.WriteLine($"Sẽ tạo restApiUser name={userName}, name_erp={nameErp}, status=1, forms=[{string.Join(", ", forms)}], issue-key={issueKey}");
            if (!apply)
            {
                Console.WriteLine("Kiểm thử (chưa ghi). Thêm --apply để tạo.");
                return 0;
            }

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            int uId;
            await using (var insert = new SqlCommand("""
                INSERT INTO restApiUser ([name], [password], name_erp, [status])
                VALUES (@name, @password, @name_erp, '1');
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("@name", userName);
                insert.Parameters.Add("@password", SqlDbType.VarChar, 256).Value = cipher;
                insert.Parameters.AddWithValue("@name_erp", nameErp);
                uId = (int)(await insert.ExecuteScalarAsync())!;
            }

            foreach (string form in forms)
            {
                await using var allow = new SqlCommand(
                    "INSERT INTO restApiAllowForm (u_id, form) VALUES (@u_id, @form)", connection, transaction);
                allow.Parameters.AddWithValue("@u_id", uId);
                allow.Parameters.AddWithValue("@form", form);
                await allow.ExecuteNonQueryAsync();
            }

            string? privateKeyBase64 = null;
            if (protector != null)
            {
                byte[] privateKey = PrivateKeyProtector.GeneratePrivateKey();
                await using var update = new SqlCommand(
                    "UPDATE restApiUser SET private_key = @private_key WHERE id = @id", connection, transaction);
                update.Parameters.AddWithValue("@private_key", protector.Protect(privateKey, uId));
                update.Parameters.AddWithValue("@id", uId);
                await update.ExecuteNonQueryAsync();
                privateKeyBase64 = Convert.ToBase64String(privateKey);
            }

            await transaction.CommitAsync();

            Console.WriteLine($"Đã tạo '{userName}' (u_id {uId}). Đăng nhập portal bằng name + mật khẩu vừa đặt.");
            if (privateKeyBase64 != null)
            {
                Console.WriteLine("private_key (giao đối tác một lần, không hiện lại):");
                Console.WriteLine(privateKeyBase64);
            }
            return 0;
        }
    }
}
