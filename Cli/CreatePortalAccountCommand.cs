using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Models;
using Security;

namespace Cli
{
    /// <summary>
    /// Tạo tài khoản đăng nhập portal trung tâm (DB portal riêng): nội bộ (staff) hoặc khách hàng (customer) gắn vào dự án.
    /// Mật khẩu đọc từ biến môi trường, lưu băm PBKDF2. Quy tắc vai trò/dự án dùng chung với portal (Models.PortalMembershipRules)
    /// </summary>
    public static class CreatePortalAccountCommand
    {
        public static async Task<int> RunAsync(string[] args)
        {
            string? configPath = null, login = null, display = null, kind = null, passwordEnv = null, project = null, role = null, projectUser = null;
            bool apply = false, allowWeakPassword = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config": configPath = CliArgs.Next(args, ref i); break;
                    case "--login": login = CliArgs.Next(args, ref i); break;
                    case "--name": display = CliArgs.Next(args, ref i); break;
                    case "--kind": kind = CliArgs.Next(args, ref i); break;
                    case "--password-env": passwordEnv = CliArgs.Next(args, ref i); break;
                    case "--project": project = CliArgs.Next(args, ref i); break;
                    case "--role": role = CliArgs.Next(args, ref i); break;
                    case "--project-user": projectUser = CliArgs.Next(args, ref i); break;
                    case "--apply": apply = true; break;
                    case "--allow-weak-password": allowWeakPassword = true; break;
                    default: throw new ArgumentException($"Tham số không hỗ trợ: {args[i]}");
                }
            }

            if (configPath == null || !File.Exists(configPath))
            {
                throw new ArgumentException("Thiếu --config hoặc file không tồn tại");
            }
            if (string.IsNullOrWhiteSpace(login) || login.Length > 64 || login.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@')))
            {
                throw new ArgumentException("Thiếu --login (tối đa 64 ký tự: chữ, số, . - _ @)");
            }
            if (string.IsNullOrWhiteSpace(display) || display.Length > 100)
            {
                throw new ArgumentException("Thiếu --name <tên hiển thị> (tối đa 100 ký tự)");
            }
            if (kind is not (PortalMembershipRules.Staff or PortalMembershipRules.Customer))
            {
                throw new ArgumentException("--kind phải là staff hoặc customer");
            }
            role ??= kind == PortalMembershipRules.Customer ? "partner" : null;
            if (role is not ("partner" or "deployment" or "administrator"))
            {
                throw new ArgumentException("Thiếu --role (partner | deployment | administrator)");
            }
            if (project != null && project.Length > 32)
            {
                throw new ArgumentException("--project là mã dự án (tối đa 32 ký tự)");
            }
            if (projectUser != null && (project == null || kind != PortalMembershipRules.Customer || projectUser.Length > 32))
            {
                throw new ArgumentException("--project-user (restApiUser của khách) chỉ dùng cho khách hàng có --project, tối đa 32 ký tự");
            }
            string roleScope = role == "partner" ? PortalMembershipRules.Customer : PortalMembershipRules.Staff;
            if (!PortalMembershipRules.IsValid(kind, roleScope, role, project != null))
            {
                throw new ArgumentException(kind == PortalMembershipRules.Customer
                    ? "Khách hàng chỉ có vai trò partner và phải có --project"
                    : "Staff chỉ có deployment/administrator; deployment phải có --project, administrator không --project là quyền mọi dự án");
            }
            if (string.IsNullOrWhiteSpace(passwordEnv))
            {
                throw new ArgumentException("Thiếu --password-env <tên biến môi trường chứa mật khẩu>");
            }
            string? password = Environment.GetEnvironmentVariable(passwordEnv);
            // Mặc định ≥ 12 ký tự. --allow-weak-password (chỉ cho môi trường thử / nội bộ) hạ xuống 6 ký tự, in cảnh báo và ghi vào audit
            int minimum = allowWeakPassword ? 6 : 12;
            if (string.IsNullOrEmpty(password) || password.Length < minimum || password.Length > 256)
            {
                throw new ArgumentException($"Biến môi trường '{passwordEnv}' phải có mật khẩu dài {minimum}–256 ký tự");
            }
            if (allowWeakPassword && password.Length < 12)
            {
                Console.Error.WriteLine("CẢNH BÁO: mật khẩu ngắn hơn 12 ký tự (--allow-weak-password). Chỉ dùng cho môi trường thử; đổi ngay ở trang Đổi mật khẩu của portal (yêu cầu ≥ 12 ký tự).");
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(Path.GetFullPath(configPath), optional: false)
                .AddEnvironmentVariables()
                .Build();
            // Cùng quy ước với portal (WebPortal): ưu tiên ControlPlane:ConnectionString, không có thì connectionStrings:appConnectionString
            string connectionString = config["ControlPlane:ConnectionString"] is { Length: > 0 } dedicated ? dedicated : config["connectionStrings:appConnectionString"] ?? "";
            if (connectionString.Length == 0)
            {
                throw new ArgumentException("Thiếu ControlPlane:ConnectionString hoặc connectionStrings:appConnectionString (DB portal riêng)");
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using (var exists = new SqlCommand("SELECT COUNT(1) FROM portalAccount WHERE login = @login", connection))
            {
                exists.Parameters.AddWithValue("@login", login);
                if ((int)(await exists.ExecuteScalarAsync())! > 0)
                {
                    Console.Error.WriteLine($"Đã có tài khoản portal '{login}'.");
                    return 1;
                }
            }

            int? projectId = null;
            if (project != null)
            {
                await using var find = new SqlCommand("SELECT id FROM portalProject WHERE code = @code AND status = '1'", connection);
                find.Parameters.AddWithValue("@code", project);
                object? found = await find.ExecuteScalarAsync();
                if (found == null)
                {
                    Console.Error.WriteLine($"Không có dự án '{project}' còn hoạt động trong portalProject.");
                    return 1;
                }
                projectId = (int)found;
            }

            int roleId;
            await using (var findRole = new SqlCommand("SELECT id FROM portalRole WHERE code = @code AND scope = @scope", connection))
            {
                findRole.Parameters.AddWithValue("@code", role);
                findRole.Parameters.AddWithValue("@scope", roleScope);
                object? found = await findRole.ExecuteScalarAsync();
                if (found == null)
                {
                    Console.Error.WriteLine($"Chưa có vai trò '{role}' (chạy sql/portal/001).");
                    return 1;
                }
                roleId = (int)found;
            }

            Console.WriteLine($"Sẽ tạo tài khoản portal login={login}, kind={kind}, vai trò={role}, dự án={(project ?? "(mọi dự án)")}{(projectUser != null ? ", tài khoản API=" + projectUser : "")}");
            if (!apply)
            {
                Console.WriteLine("Kiểm thử (chưa ghi). Thêm --apply để tạo.");
                return 0;
            }

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            int accountId;
            await using (var insert = new SqlCommand("""
                INSERT INTO portalAccount (login, display_name, kind, status, password_hash) VALUES (@login, @name, @kind, '1', @hash);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("@login", login);
                insert.Parameters.AddWithValue("@name", display);
                insert.Parameters.AddWithValue("@kind", kind);
                insert.Parameters.AddWithValue("@hash", PortalPasswordHasher.Hash(password));
                accountId = (int)(await insert.ExecuteScalarAsync())!;
            }

            await using (var membership = new SqlCommand("""
                INSERT INTO portalMembership (account_id, project_id, role_id, project_user_name) VALUES (@account, @project, @role, @user)
                """, connection, transaction))
            {
                membership.Parameters.AddWithValue("@account", accountId);
                membership.Parameters.AddWithValue("@project", (object?)projectId ?? DBNull.Value);
                membership.Parameters.AddWithValue("@role", roleId);
                membership.Parameters.AddWithValue("@user", (object?)projectUser ?? DBNull.Value);
                await membership.ExecuteNonQueryAsync();
            }

            await using (var audit = new SqlCommand("""
                INSERT INTO portalAudit (actor_account_id, project_id, action, target, outcome, detail) VALUES (NULL, @project, 'account.create', @login, 'SUCCESS', @detail)
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue("@project", (object?)projectId ?? DBNull.Value);
                audit.Parameters.AddWithValue("@login", login);
                audit.Parameters.AddWithValue("@detail", allowWeakPassword && password.Length < 12 ? "tạo bằng Cli (cho phép mật khẩu ngắn)" : "tạo bằng Cli");
                await audit.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            Console.WriteLine($"Đã tạo tài khoản portal '{login}' (id {accountId}). Mật khẩu không in lại.");
            return 0;
        }
    }
}
