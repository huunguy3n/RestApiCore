using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Security;

namespace Cli
{
    /// <summary>
    /// Cấp private_key (HMAC v3) cho một tài khoản restApiUser. Khóa in ra đúng một lần, server chỉ giữ bản mã
    /// </summary>
    public static class IssueKeyCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            string? configPath = null, userName = null;
            bool apply = false, rotate = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": configPath = CliArgs.Next(args, ref i); break;
                    case "--user": userName = CliArgs.Next(args, ref i); break;
                    case "--rotate": rotate = true; break;
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

            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .AddEnvironmentVariables()
                .Build();

            string connectionString = config.GetConnectionString("appConnectionString") ?? "";
            if (connectionString.Length == 0)
            {
                throw new ArgumentException("Thiếu connectionStrings:appConnectionString");
            }
            var protector = new PrivateKeyProtector(config["Security:KeyEncryptionKey"] ?? "");

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            int uId;
            bool hasKey;
            await using (var select = new SqlCommand("SELECT id, private_key FROM restApiUser WITH (UPDLOCK, HOLDLOCK) WHERE [name] = @name", connection, transaction))
            {
                select.Parameters.AddWithValue("@name", userName);
                await using var reader = await select.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    Console.Error.WriteLine($"Không có tài khoản restApiUser '{userName}'.");
                    return 1;
                }
                uId = reader.GetInt32(0);
                hasKey = !reader.IsDBNull(1) && reader.GetString(1).Length > 0;
            }

            if (hasKey && !rotate)
            {
                Console.Error.WriteLine($"'{userName}' đã có private_key. Thêm --rotate để cấp khóa mới (khóa cũ hết hiệu lực ngay).");
                return 1;
            }
            if (!apply)
            {
                Console.WriteLine($"Kiểm thử: sẽ {(hasKey ? "thay" : "cấp")} private_key cho '{userName}' (u_id {uId}). Thêm --apply để ghi.");
                return 0;
            }

            byte[] privateKey = PrivateKeyProtector.GeneratePrivateKey();
            await using (var update = new SqlCommand("UPDATE restApiUser SET private_key = @private_key WHERE id = @id", connection, transaction))
            {
                update.Parameters.AddWithValue("@private_key", protector.Protect(privateKey, uId));
                update.Parameters.AddWithValue("@id", uId);
                await update.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();

            Console.WriteLine($"Đã {(hasKey ? "thay" : "cấp")} private_key cho '{userName}' (u_id {uId}).");
            Console.WriteLine("Giao khóa dưới đây cho đối tác qua kênh an toàn. Khóa không lưu ở đâu khác và không hiện lại:");
            Console.WriteLine(Convert.ToBase64String(privateKey));
            return 0;
        }
    }

    public static class CliArgs
    {
        public static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Thiếu giá trị cho {args[i]}");
            }
            return args[++i];
        }
    }
}
