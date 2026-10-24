using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Provider;
using Security;

namespace DataAccess
{
    public sealed record ErpUser(int Id, string Name, bool Admin, bool Active);

    /// <summary>Trạng thái restApiUser trên DB dự án (không có khóa, không có mật khẩu)</summary>
    public sealed record ProjectApiUser(int UId, string Name, string NameErp, bool Active, bool HasKey, DateTime? KeyIssuedAt, DateTime? TokenValidAfter,
        DateTime? SyncedAt, IReadOnlyList<string> Forms, IReadOnlyList<string> Ips);

    /// <summary>Bản muốn có của một user Api (portal là nguồn sự thật, hoặc PortalApp ở PROD)</summary>
    public sealed record ApiUserSpec(string Name, string NameErp, bool Active, IReadOnlyCollection<string> Forms, IReadOnlyCollection<(string Ip, string? Description)> Ips);

    public sealed record AuditEntry(string Source, string Action, string? Target, string Outcome, string? ActorName, string? Detail,
        string? WindowsUser = null, string? MachineName = null);

    /// <summary>
    /// Thao tác quản trị trên DB của MỘT dự án (restApiUser, restApiAllowForm, restApiAllowIp, restApiAudit, vsysuserinfo).
    /// WebPortal (DEV/UAT, connection string đọc từ folder restApi) và PortalApp (PROD) dùng chung. Mọi giá trị đi bằng tham số
    /// </summary>
    public sealed class ProjectAdminRepository(SqlDatabase database)
    {
        public const int MaxForms = 500;
        public const int MaxIps = 100;

        /// <summary>Kết nối được, có restApiUser và đã chạy sql/009 (cột token_valid_after); trả mô tả lỗi nếu chưa</summary>
        public async Task<string?> CheckSchemaAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT CASE WHEN OBJECT_ID('dbo.restApiUser', 'U') IS NULL THEN 0 ELSE 1 END AS has_user,
                	CASE WHEN COL_LENGTH('dbo.restApiUser', 'token_valid_after') IS NULL THEN 0 ELSE 1 END AS has_009,
                	CASE WHEN OBJECT_ID('dbo.vsysuserinfo') IS NULL THEN 0 ELSE 1 END AS has_erp
                """, cancellationToken);
            DataRow row = ds.Tables[0].Rows[0];
            if (Convert.ToInt32(row["has_user"]) == 0)
            {
                return "DB chưa có bảng restApiUser (chạy sql/000-009)";
            }
            if (Convert.ToInt32(row["has_009"]) == 0)
            {
                return "DB chưa chạy sql/009_restApiPortalSupport.sql";
            }
            return Convert.ToInt32(row["has_erp"]) == 0 ? "DB không có vsysuserinfo (không phải DB app FBO?)" : null;
        }

        /// <summary>NSD FBO để map name_erp (chỉ tên, id, admin, trạng thái)</summary>
        public async Task<IReadOnlyList<ErpUser>> ListErpUsersAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT a.id, RTRIM(a.[name]) AS [name], a.admin, a.[status] FROM dbo.vsysuserinfo a ORDER BY a.[name]", cancellationToken);
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new ErpUser(Convert.ToInt32(r["id"]), r["name"].ToString()!,
                r["admin"] != DBNull.Value && Convert.ToInt32(r["admin"]) == 1, r["status"].ToString()?.Trim() == "1")).ToList();
        }

        public async Task<ErpUser?> FindErpUserAsync(string nameErp, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT TOP 1 a.id, RTRIM(a.[name]) AS [name], a.admin, a.[status] FROM dbo.vsysuserinfo a WHERE a.[name] = @name",
                cancellationToken, new SqlParameter("@name", SqlDbType.VarChar, 30) { Value = nameErp });
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new ErpUser(Convert.ToInt32(r["id"]), r["name"].ToString()!,
                r["admin"] != DBNull.Value && Convert.ToInt32(r["admin"]) == 1, r["status"].ToString()?.Trim() == "1")).FirstOrDefault();
        }

        public async Task<IReadOnlyList<ProjectApiUser>> ListUsersAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT a.id, a.[name], a.name_erp, a.[status], CASE WHEN a.private_key IS NULL OR a.private_key = '' THEN 0 ELSE 1 END AS has_key
                	 , a.key_issued_at, a.token_valid_after, a.synced_at
                	FROM dbo.restApiUser a
                	ORDER BY a.[name]

                SELECT a.u_id, a.form FROM dbo.restApiAllowForm a

                SELECT a.u_id, a.ip_address FROM dbo.restApiAllowIp a
                """, cancellationToken);
            ILookup<int, string> forms = ds.Tables[1].Rows.Cast<DataRow>().ToLookup(r => Convert.ToInt32(r["u_id"]), r => r["form"].ToString()!.Trim());
            ILookup<int, string> ips = ds.Tables[2].Rows.Cast<DataRow>().ToLookup(r => Convert.ToInt32(r["u_id"]), r => r["ip_address"].ToString()!.Trim());
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r =>
            {
                int id = Convert.ToInt32(r["id"]);
                return new ProjectApiUser(id, r["name"].ToString()!.Trim(), r["name_erp"].ToString()!.Trim(), r["status"].ToString()?.Trim() == "1",
                    Convert.ToInt32(r["has_key"]) == 1, r["key_issued_at"] as DateTime?, r["token_valid_after"] as DateTime?, r["synced_at"] as DateTime?,
                    forms[id].Order(StringComparer.OrdinalIgnoreCase).ToList(), ips[id].Order(StringComparer.Ordinal).ToList());
            }).ToList();
        }

        public async Task<ProjectApiUser?> FindUserAsync(string name, CancellationToken cancellationToken = default) =>
            (await ListUsersAsync(cancellationToken)).FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Ghi đúng trạng thái mong muốn của một user trong một transaction: tạo/cập nhật restApiUser, thay TOÀN BỘ restApiAllowForm
        /// và restApiAllowIp của user (danh sách rỗng = xóa hết = được tất cả). Trả u_id
        /// </summary>
        public async Task<int> UpsertUserAsync(ApiUserSpec spec, CancellationToken cancellationToken = default)
        {
            if (spec.Forms.Count > MaxForms || spec.Ips.Count > MaxIps)
            {
                throw new ArgumentException("Danh sách form/IP quá dài");
            }

            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = await database.BeginTransactionAsync(connection, cancellationToken);
            try
            {
                int uId = Convert.ToInt32(await database.ExecuteScalarAsync(connection, transaction, """
                    DECLARE @id INT

                    SELECT @id = a.id FROM dbo.restApiUser a WITH (UPDLOCK, HOLDLOCK) WHERE a.[name] = @name

                    IF @id IS NULL BEGIN
                    	INSERT INTO dbo.restApiUser ([name], [password], name_erp, [status], synced_at) VALUES (@name, '', @name_erp, @status, GETDATE())

                    	SELECT @id = CAST(SCOPE_IDENTITY() AS INT)
                    END
                    ELSE
                    	UPDATE dbo.restApiUser SET name_erp = @name_erp, [status] = @status, synced_at = GETDATE() WHERE id = @id

                    -- Khóa tài khoản: mọi JWT đang có hết hiệu lực
                    IF @status <> '1' UPDATE dbo.restApiUser SET token_valid_after = GETUTCDATE() WHERE id = @id

                    DELETE FROM dbo.restApiAllowForm WHERE u_id = @id

                    DELETE FROM dbo.restApiAllowIp WHERE u_id = @id

                    SELECT @id
                    """, cancellationToken,
                    new SqlParameter("@name", SqlDbType.VarChar, 32) { Value = spec.Name },
                    new SqlParameter("@name_erp", SqlDbType.VarChar, 30) { Value = spec.NameErp },
                    new SqlParameter("@status", SqlDbType.Char, 1) { Value = spec.Active ? "1" : "0" }));

                foreach (string form in spec.Forms.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    await database.ExecuteAsync(connection, transaction, "INSERT INTO dbo.restApiAllowForm (u_id, form) VALUES (@u_id, @form)", cancellationToken,
                        new SqlParameter("@u_id", SqlDbType.Int) { Value = uId },
                        new SqlParameter("@form", SqlDbType.VarChar, 32) { Value = form });
                }
                foreach (var ip in spec.Ips.DistinctBy(i => i.Ip, StringComparer.Ordinal))
                {
                    await database.ExecuteAsync(connection, transaction, "INSERT INTO dbo.restApiAllowIp (u_id, ip_address, [description]) VALUES (@u_id, @ip, @description)", cancellationToken,
                        new SqlParameter("@u_id", SqlDbType.Int) { Value = uId },
                        new SqlParameter("@ip", SqlDbType.VarChar, 45) { Value = ip.Ip },
                        new SqlParameter("@description", SqlDbType.NVarChar, 100) { Value = (object?)ip.Description ?? DBNull.Value });
                }

                await transaction.CommitAsync(cancellationToken);
                return uId;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        /// <summary>Cấp hoặc xoay private_key. Khóa cũ hết hiệu lực ngay; trả khóa gốc (32 byte) để hiện đúng một lần. Null nếu không có user</summary>
        public async Task<byte[]?> IssueKeyAsync(string name, PrivateKeyProtector protector, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = await database.BeginTransactionAsync(connection, cancellationToken);
            object? value = await database.ExecuteScalarAsync(connection, transaction,
                "SELECT a.id FROM dbo.restApiUser a WITH (UPDLOCK, HOLDLOCK) WHERE a.[name] = @name", cancellationToken,
                new SqlParameter("@name", SqlDbType.VarChar, 32) { Value = name });
            if (value == null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
            int uId = Convert.ToInt32(value);

            byte[] privateKey = PrivateKeyProtector.GeneratePrivateKey();
            await database.ExecuteAsync(connection, transaction,
                "UPDATE dbo.restApiUser SET private_key = @key, key_issued_at = GETDATE() WHERE id = @id", cancellationToken,
                new SqlParameter("@key", SqlDbType.NVarChar, 512) { Value = protector.Protect(privateKey, uId) },
                new SqlParameter("@id", SqlDbType.Int) { Value = uId });
            await transaction.CommitAsync(cancellationToken);
            return privateKey;
        }

        /// <summary>Giải private_key hiện tại (user Api xem lại khóa của chính mình). Null nếu chưa cấp</summary>
        public async Task<byte[]?> ReadKeyAsync(string name, PrivateKeyProtector protector, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT a.id, a.private_key FROM dbo.restApiUser a WHERE a.[name] = @name", cancellationToken,
                new SqlParameter("@name", SqlDbType.VarChar, 32) { Value = name });
            if (ds.Tables[0].Rows.Count == 0 || ds.Tables[0].Rows[0]["private_key"] is not string protectedKey || protectedKey.Length == 0)
            {
                return null;
            }
            try
            {
                return protector.Unprotect(protectedKey, Convert.ToInt32(ds.Tables[0].Rows[0]["id"]));
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>Thu hồi mọi JWT đã phát cho user (token_valid_after = giờ UTC của SQL). False nếu không có user</summary>
        public async Task<bool> RevokeTokensAsync(string name, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                UPDATE dbo.restApiUser SET token_valid_after = GETUTCDATE() WHERE [name] = @name

                SELECT @@ROWCOUNT AS n
                """, cancellationToken, new SqlParameter("@name", SqlDbType.VarChar, 32) { Value = name });
            return Convert.ToInt32(ds.Tables[^1].Rows[0][0]) == 1;
        }

        public Task WriteAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            database.ExecuteAsync("""
                INSERT INTO dbo.restApiAudit (actor_name, action, target_type, target, outcome, detail, source, machine_name, windows_user)
                	VALUES (@actor, @action, 'user', @target, @outcome, @detail, @source, @machine, @windows)
                """, cancellationToken,
                new SqlParameter("@actor", SqlDbType.VarChar, 32) { Value = (object?)Cut(entry.ActorName, 32) ?? DBNull.Value },
                new SqlParameter("@action", SqlDbType.VarChar, 64) { Value = Cut(entry.Action, 64)! },
                new SqlParameter("@target", SqlDbType.VarChar, 128) { Value = (object?)Cut(entry.Target, 128) ?? DBNull.Value },
                new SqlParameter("@outcome", SqlDbType.VarChar, 16) { Value = Cut(entry.Outcome, 16)! },
                new SqlParameter("@detail", SqlDbType.NVarChar, 1024) { Value = (object?)Cut(entry.Detail, 1024) ?? DBNull.Value },
                new SqlParameter("@source", SqlDbType.VarChar, 16) { Value = Cut(entry.Source, 16)! },
                new SqlParameter("@machine", SqlDbType.NVarChar, 64) { Value = (object?)Cut(entry.MachineName, 64) ?? DBNull.Value },
                new SqlParameter("@windows", SqlDbType.NVarChar, 128) { Value = (object?)Cut(entry.WindowsUser, 128) ?? DBNull.Value });

        /// <summary>Ghi nhận license đã kích hoạt (tra soát)</summary>
        public Task RecordLicenseAsync(Guid licenseId, string projectCode, string macs, string? machineName, DateTime? expiresAt, string? windowsUser, CancellationToken cancellationToken = default) =>
            database.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM dbo.restApiLicense a WHERE a.license_id = @id)
                	INSERT INTO dbo.restApiLicense (license_id, ma_da, mac, machine_name, expires_at, activated_by_windows_user)
                		VALUES (@id, @ma_da, @mac, @machine, @expires, @windows)
                """, cancellationToken,
                new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = licenseId },
                new SqlParameter("@ma_da", SqlDbType.VarChar, 32) { Value = projectCode },
                new SqlParameter("@mac", SqlDbType.VarChar, 512) { Value = Cut(macs, 512)! },
                new SqlParameter("@machine", SqlDbType.NVarChar, 64) { Value = (object?)Cut(machineName, 64) ?? DBNull.Value },
                new SqlParameter("@expires", SqlDbType.DateTime) { Value = (object?)expiresAt ?? DBNull.Value },
                new SqlParameter("@windows", SqlDbType.NVarChar, 128) { Value = (object?)Cut(windowsUser, 128) ?? DBNull.Value });

        private static string? Cut(string? value, int max)
        {
            string? clean = value == null ? null : new string(value.Where(c => !char.IsControl(c)).ToArray());
            return clean != null && clean.Length > max ? clean[..max] : clean;
        }
    }
}
