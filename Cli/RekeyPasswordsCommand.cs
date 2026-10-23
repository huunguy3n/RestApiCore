using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Security;

namespace Cli
{
    /// <summary>
    /// Mã hóa lại restApiUser.password sang passwordKey (KE_HOACH mục 8.1). Chạy lại nhiều lần không lỗi:
    /// dòng đã theo khóa mới thì bỏ qua
    /// </summary>
    public static class RekeyPasswordsCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            string? configPath = null, oldKeyName = "appSettings:secretKey", newKeyName = "appSettings:passwordKey";
            bool apply = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": configPath = CliArgs.Next(args, ref i); break;
                    case "--old-key": oldKeyName = CliArgs.Next(args, ref i); break;
                    case "--new-key": newKeyName = CliArgs.Next(args, ref i); break;
                    case "--apply": apply = true; break;
                    default: throw new ArgumentException($"Tham số không hỗ trợ: {args[i]}");
                }
            }

            if (configPath == null || !File.Exists(configPath))
            {
                throw new ArgumentException("Thiếu --config hoặc file không tồn tại");
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .AddEnvironmentVariables()
                .Build();

            string connectionString = config.GetConnectionString("appConnectionString") ?? "";
            string oldKey = config[oldKeyName] ?? "";
            string newKey = config[newKeyName] ?? "";

            if (connectionString.Length == 0)
            {
                throw new ArgumentException("Thiếu connectionStrings:appConnectionString");
            }
            if (oldKey.Length != 32 || newKey.Length != 32)
            {
                throw new ArgumentException($"{oldKeyName} và {newKeyName} phải đúng 32 ký tự");
            }
            if (oldKey == newKey)
            {
                throw new ArgumentException($"{newKeyName} trùng {oldKeyName}, không có gì để mã hóa lại");
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            // Khóa các dòng tới khi xong để không ai đổi mật khẩu giữa lúc đọc và ghi
            var rows = new List<(string Name, string? Cipher)>();
            await using (var select = new SqlCommand("SELECT [name], [password] FROM restApiUser WITH (UPDLOCK, HOLDLOCK)", connection, transaction))
            await using (var reader = await select.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }

            var results = rows.Select(r => (r.Name, r.Cipher, Result: PasswordRekey.Rekey(r.Cipher, oldKey, newKey))).ToList();

            foreach (var group in results.GroupBy(r => r.Result.Status).OrderBy(g => g.Key))
            {
                Console.WriteLine($"{group.Key,-12} {group.Count()}");
            }

            var blocked = results.Where(r => r.Result.Status is RekeyStatus.Ambiguous or RekeyStatus.Unreadable).ToList();
            if (blocked.Count > 0)
            {
                // Chỉ in tên tài khoản, không in mật khẩu hay bản mã
                foreach (var r in blocked)
                {
                    Console.Error.WriteLine($"  {r.Result.Status}: {r.Name.TrimEnd()}");
                }
                Console.Error.WriteLine("Dừng, không ghi gì. Sửa các tài khoản trên rồi chạy lại.");
                return 1;
            }

            var toUpdate = results.Where(r => r.Result.Status == RekeyStatus.Rekeyed).ToList();
            if (!apply)
            {
                Console.WriteLine($"Kiểm thử: sẽ mã hóa lại {toUpdate.Count} tài khoản. Thêm --apply để ghi.");
                return 0;
            }

            foreach (var r in toUpdate)
            {
                await using var update = new SqlCommand("UPDATE restApiUser SET [password] = @new_password WHERE [name] = @name AND [password] = @old_password", connection, transaction);
                update.Parameters.AddWithValue("@new_password", r.Result.NewCipher);
                update.Parameters.AddWithValue("@name", r.Name);
                update.Parameters.AddWithValue("@old_password", r.Cipher);

                if (await update.ExecuteNonQueryAsync() != 1)
                {
                    await transaction.RollbackAsync();
                    Console.Error.WriteLine($"Không cập nhật được {r.Name.TrimEnd()}, đã rollback toàn bộ.");
                    return 1;
                }
            }

            await transaction.CommitAsync();
            Console.WriteLine($"Đã mã hóa lại {toUpdate.Count} tài khoản.");
            return 0;
        }
    }
}
