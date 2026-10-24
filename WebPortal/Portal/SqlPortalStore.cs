using System.Data;
using Microsoft.Data.SqlClient;
using Provider;

namespace WebPortal.Portal
{
    /// <summary>
    /// PortalDb trên SQL Server (2008 trở lên). Mọi giá trị đi bằng tham số. Chưa khai Portal:ConnectionString thì mọi lệnh ném
    /// InvalidOperationException để trang hiện "chưa cấu hình" thay vì lỗi 500
    /// </summary>
    public sealed partial class SqlPortalStore(string connectionString) : IPortalStore
    {
        private readonly SqlDatabase? _database = connectionString.Length > 0 ? new SqlDatabase(connectionString, 30) : null;

        private SqlDatabase Db => _database ?? throw new InvalidOperationException("Chưa khai Portal:ConnectionString");

        private static SqlParameter P(string name, SqlDbType type, object? value, int size = 0) =>
            size != 0 ? new SqlParameter(name, type, size) { Value = value ?? DBNull.Value } : new SqlParameter(name, type) { Value = value ?? DBNull.Value };

        public async Task<string?> CheckSchemaAsync(CancellationToken cancellationToken = default)
        {
            if (_database == null)
            {
                return "Chưa khai Portal:ConnectionString trong appsettings.Local.json.";
            }
            try
            {
                DataSet ds = await Db.RetrieveAsync("""
                    SELECT CASE WHEN OBJECT_ID('dbo.portalAccount', 'U') IS NULL THEN 0 ELSE 1 END AS has_account,
                    	CASE WHEN OBJECT_ID('dbo.portalApiUser', 'U') IS NULL THEN 0 ELSE 1 END AS has_api_user,
                    	CASE WHEN OBJECT_ID('dbo.portalProject', 'U') IS NULL THEN 0 ELSE 1 END AS has_project,
                    	CASE WHEN OBJECT_ID('dbo.portalSession', 'U') IS NULL THEN 0 ELSE 1 END AS has_session
                    """, cancellationToken);
                DataRow row = ds.Tables[0].Rows[0];
                if (Convert.ToInt32(row["has_account"]) == 0 || Convert.ToInt32(row["has_project"]) == 0
                    || Convert.ToInt32(row["has_api_user"]) == 0 || Convert.ToInt32(row["has_session"]) == 0)
                {
                    return "PortalDb chưa chạy sql/portal/001_portalDb.sql (thiếu portalAccount / portalApiUser / portalProject / portalSession).";
                }
                return null;
            }
            catch (DatabaseException)
            {
                return "Không truy vấn được PortalDb (kiểm tra Portal:ConnectionString).";
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                return "Không kết nối được PortalDb (kiểm tra Portal:ConnectionString).";
            }
        }

        /// <summary>Bỏ ký tự điều khiển rồi cắt: chuỗi do người dùng gửi lên không làm giả được dòng nhật ký</summary>
        private static string? Cut(string? value, int max)
        {
            string? clean = value == null ? null : new string(value.Where(c => !char.IsControl(c)).ToArray());
            return clean != null && clean.Length > max ? clean[..max] : clean;
        }

        private static DateTime? Date(object value) => value == DBNull.Value ? null : (DateTime)value;

        // ---------- Tài khoản ----------
        private const string AccountColumns = "a.id, a.login, a.display_name, a.kind, a.status, a.password_hash, a.must_change_password, a.last_login_at";

        private static PortalAccount MapAccount(DataRow r) => new(Convert.ToInt32(r["id"]), r["login"].ToString()!.Trim(), r["display_name"].ToString()!,
            r["kind"].ToString()!.Trim(), r["status"].ToString()!.Trim() == "1", r["password_hash"].ToString()!, Convert.ToBoolean(r["must_change_password"]), Date(r["last_login_at"]));

        public async Task<PortalAccount?> FindAccountByLoginAsync(string login, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {AccountColumns} FROM dbo.portalAccount a WHERE a.login = @login", cancellationToken, P("@login", SqlDbType.VarChar, login, 64));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapAccount).FirstOrDefault();
        }

        public async Task<PortalAccount?> FindAccountAsync(int id, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {AccountColumns} FROM dbo.portalAccount a WHERE a.id = @id", cancellationToken, P("@id", SqlDbType.Int, id));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapAccount).FirstOrDefault();
        }

        public async Task<bool> SuperAdminExistsAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("SELECT COUNT(1) FROM dbo.portalAccount a WHERE a.kind = 'SA'", cancellationToken);
            return Convert.ToInt32(ds.Tables[0].Rows[0][0]) > 0;
        }

        public async Task<IReadOnlyList<PortalAccount>> ListAdminsAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {AccountColumns} FROM dbo.portalAccount a WHERE a.kind = 'ADMIN' ORDER BY a.login", cancellationToken);
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapAccount).ToList();
        }

        private const string NameTakenSql = """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.portalAccount a WITH (UPDLOCK, HOLDLOCK) WHERE a.login = @login)
            	OR EXISTS (SELECT 1 FROM dbo.portalApiUser b WITH (UPDLOCK, HOLDLOCK) WHERE b.user_name = @login) THEN 1 ELSE 0 END
            """;

        public async Task<bool> LoginNameTakenAsync(string login, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync(NameTakenSql.Replace("WITH (UPDLOCK, HOLDLOCK)", ""), cancellationToken, P("@login", SqlDbType.VarChar, login, 64));
            return Convert.ToInt32(ds.Tables[0].Rows[0][0]) == 1;
        }

        public async Task<int?> CreateAccountAsync(string login, string displayName, string kind, string passwordHash, int? createdBy, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await Db.OpenConnectionAsync(cancellationToken);
            // Kiểm trùng tên ở cả hai bảng và ghi trong cùng transaction Serializable: hai người tạo cùng tên thì chỉ một người được
            await using SqlTransaction transaction = await Db.BeginTransactionAsync(connection, IsolationLevel.Serializable, cancellationToken);
            object? taken = await Db.ExecuteScalarAsync(connection, transaction,
                NameTakenSql + " + CASE WHEN @kind = 'SA' AND EXISTS (SELECT 1 FROM dbo.portalAccount c WITH (UPDLOCK, HOLDLOCK) WHERE c.kind = 'SA') THEN 1 ELSE 0 END",
                cancellationToken, P("@login", SqlDbType.VarChar, login, 64), P("@kind", SqlDbType.VarChar, kind, 10));
            if (Convert.ToInt32(taken) > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
            int id = Convert.ToInt32(await Db.ExecuteScalarAsync(connection, transaction, """
                INSERT INTO dbo.portalAccount (login, display_name, kind, status, password_hash, must_change_password) VALUES (@login, @name, @kind, '1', @hash, 1)

                SELECT CAST(SCOPE_IDENTITY() AS INT)
                """, cancellationToken,
                P("@login", SqlDbType.VarChar, login, 64), P("@name", SqlDbType.NVarChar, Cut(displayName, 100), 100),
                P("@kind", SqlDbType.VarChar, kind, 10), P("@hash", SqlDbType.VarChar, passwordHash, 256)));
            await transaction.CommitAsync(cancellationToken);
            return id;
        }

        public Task SetAccountActiveAsync(int id, bool active, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalAccount SET status = @status WHERE id = @id", cancellationToken, P("@status", SqlDbType.Char, active ? "1" : "0", 1), P("@id", SqlDbType.Int, id));

        public Task SetAccountPasswordAsync(int id, string passwordHash, bool mustChange, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalAccount SET password_hash = @hash, must_change_password = @must, password_changed_at = GETDATE() WHERE id = @id", cancellationToken,
                P("@hash", SqlDbType.VarChar, passwordHash, 256), P("@must", SqlDbType.Bit, mustChange), P("@id", SqlDbType.Int, id));

        public Task TouchAccountLoginAsync(int id, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalAccount SET last_login_at = GETDATE() WHERE id = @id", cancellationToken, P("@id", SqlDbType.Int, id));

        // ---------- Dự án ----------
        private const string ProjectColumns = "a.id, a.ma_da, a.api_folder, a.api_base_url, a.status, a.last_check_at, a.last_check_result";

        private static PortalProject MapProject(DataRow r) => new(Convert.ToInt32(r["id"]), r["ma_da"].ToString()!.Trim(), r["api_folder"].ToString()!,
            r["api_base_url"].ToString()!, r["status"].ToString()!.Trim() == "1", Date(r["last_check_at"]), r["last_check_result"] as string);

        public async Task<PortalProject?> FindProjectAsync(int id, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {ProjectColumns} FROM dbo.portalProject a WHERE a.id = @id", cancellationToken, P("@id", SqlDbType.Int, id));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapProject).FirstOrDefault();
        }

        public async Task<PortalProject?> FindProjectByCodeAsync(string maDa, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {ProjectColumns} FROM dbo.portalProject a WHERE a.ma_da = @code", cancellationToken, P("@code", SqlDbType.VarChar, maDa, 32));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapProject).FirstOrDefault();
        }

        public async Task<IReadOnlyList<PortalProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {ProjectColumns} FROM dbo.portalProject a ORDER BY a.ma_da", cancellationToken);
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapProject).ToList();
        }

        public async Task<int?> CreateProjectAsync(string maDa, string apiFolder, string apiBaseUrl, int createdBy, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                IF EXISTS (SELECT 1 FROM dbo.portalProject a WHERE a.ma_da = @code) BEGIN
                	SELECT CAST(NULL AS INT)

                	RETURN
                END

                INSERT INTO dbo.portalProject (ma_da, api_folder, api_base_url, status, created_by) VALUES (@code, @folder, @url, '1', @by)

                SELECT CAST(SCOPE_IDENTITY() AS INT)
                """, cancellationToken, P("@code", SqlDbType.VarChar, maDa, 32), P("@folder", SqlDbType.NVarChar, apiFolder, 260),
                P("@url", SqlDbType.VarChar, apiBaseUrl, 256), P("@by", SqlDbType.Int, createdBy));
            object value = ds.Tables[^1].Rows[0][0];
            return value == DBNull.Value ? null : Convert.ToInt32(value);
        }

        public Task UpdateProjectAsync(int id, string apiFolder, string apiBaseUrl, bool active, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalProject SET api_folder = @folder, api_base_url = @url, status = @status WHERE id = @id", cancellationToken,
                P("@folder", SqlDbType.NVarChar, apiFolder, 260), P("@url", SqlDbType.VarChar, apiBaseUrl, 256), P("@status", SqlDbType.Char, active ? "1" : "0", 1), P("@id", SqlDbType.Int, id));

        public Task SetProjectCheckAsync(int id, string result, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalProject SET last_check_at = GETDATE(), last_check_result = @result WHERE id = @id", cancellationToken,
                P("@result", SqlDbType.NVarChar, Cut(result, 512), 512), P("@id", SqlDbType.Int, id));

        public async Task<IReadOnlyList<int>> GetAdminProjectIdsAsync(int accountId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("SELECT a.project_id FROM dbo.portalProjectAdmin a WHERE a.account_id = @id", cancellationToken, P("@id", SqlDbType.Int, accountId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => Convert.ToInt32(r[0])).ToList();
        }

        public async Task<IReadOnlyList<PortalAccount>> GetProjectAdminsAsync(int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"""
                SELECT {AccountColumns}
                	FROM dbo.portalAccount a
                		JOIN dbo.portalProjectAdmin b ON b.account_id = a.id
                	WHERE b.project_id = @p
                	ORDER BY a.login
                """, cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapAccount).ToList();
        }

        public async Task<bool> IsProjectAdminAsync(int accountId, int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("SELECT COUNT(1) FROM dbo.portalProjectAdmin a WHERE a.account_id = @a AND a.project_id = @p", cancellationToken,
                P("@a", SqlDbType.Int, accountId), P("@p", SqlDbType.Int, projectId));
            return Convert.ToInt32(ds.Tables[0].Rows[0][0]) > 0;
        }

        public Task AddProjectAdminAsync(int projectId, int accountId, int grantedBy, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM dbo.portalProjectAdmin a WHERE a.project_id = @p AND a.account_id = @a)
                	INSERT INTO dbo.portalProjectAdmin (project_id, account_id, granted_by) VALUES (@p, @a, @by)
                """, cancellationToken, P("@p", SqlDbType.Int, projectId), P("@a", SqlDbType.Int, accountId), P("@by", SqlDbType.Int, grantedBy));

        public Task RemoveProjectAdminAsync(int projectId, int accountId, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("DELETE FROM dbo.portalProjectAdmin WHERE project_id = @p AND account_id = @a", cancellationToken, P("@p", SqlDbType.Int, projectId), P("@a", SqlDbType.Int, accountId));

        // ---------- User Api ----------
        private async Task<IReadOnlyList<PortalApiUser>> LoadApiUsersAsync(string where, CancellationToken cancellationToken, params SqlParameter[] parameters)
        {
            DataSet ds = await Db.RetrieveAsync($"""
                SELECT a.id, a.project_id, a.user_name, a.name_erp, a.display_name, a.password_hash, a.status, a.sync_version, a.synced_version
                	 , a.synced_at, a.sync_error
                	FROM dbo.portalApiUser a
                	WHERE {where}
                	ORDER BY a.user_name

                SELECT b.api_user_id, b.form FROM dbo.portalApiUserForm b JOIN dbo.portalApiUser a ON a.id = b.api_user_id WHERE {where}

                SELECT b.api_user_id, b.ip_address, b.description FROM dbo.portalApiUserIp b JOIN dbo.portalApiUser a ON a.id = b.api_user_id WHERE {where}
                """, cancellationToken, parameters);
            ILookup<int, string> forms = ds.Tables[1].Rows.Cast<DataRow>().ToLookup(r => Convert.ToInt32(r[0]), r => r[1].ToString()!.Trim());
            ILookup<int, ApiUserIp> ips = ds.Tables[2].Rows.Cast<DataRow>().ToLookup(r => Convert.ToInt32(r[0]), r => new ApiUserIp(r[1].ToString()!.Trim(), r[2] as string));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r =>
            {
                int id = Convert.ToInt32(r["id"]);
                return new PortalApiUser(id, Convert.ToInt32(r["project_id"]), r["user_name"].ToString()!.Trim(), r["name_erp"].ToString()!.Trim(), r["display_name"] as string,
                    r["password_hash"].ToString()!, r["status"].ToString()!.Trim() == "1", Convert.ToInt32(r["sync_version"]), Convert.ToInt32(r["synced_version"]),
                    Date(r["synced_at"]), r["sync_error"] as string, forms[id].Order(StringComparer.OrdinalIgnoreCase).ToList(), ips[id].OrderBy(i => i.Ip, StringComparer.Ordinal).ToList());
            }).ToList();
        }

        public Task<IReadOnlyList<PortalApiUser>> ListApiUsersAsync(int projectId, CancellationToken cancellationToken = default) =>
            LoadApiUsersAsync("a.project_id = @p", cancellationToken, P("@p", SqlDbType.Int, projectId));

        public async Task<PortalApiUser?> FindApiUserAsync(int id, CancellationToken cancellationToken = default) =>
            (await LoadApiUsersAsync("a.id = @id", cancellationToken, P("@id", SqlDbType.Int, id))).FirstOrDefault();

        public async Task<PortalApiUser?> FindApiUserByNameAsync(int projectId, string userName, CancellationToken cancellationToken = default) =>
            (await LoadApiUsersAsync("a.project_id = @p AND a.user_name = @u", cancellationToken, P("@p", SqlDbType.Int, projectId), P("@u", SqlDbType.VarChar, userName, 32))).FirstOrDefault();

        public async Task<int?> CreateApiUserAsync(int projectId, NewApiUser user, int createdBy, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await Db.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = await Db.BeginTransactionAsync(connection, IsolationLevel.Serializable, cancellationToken);
            if (Convert.ToInt32(await Db.ExecuteScalarAsync(connection, transaction, NameTakenSql, cancellationToken,
                    P("@login", SqlDbType.VarChar, user.UserName, 64))) > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
            int id = Convert.ToInt32(await Db.ExecuteScalarAsync(connection, transaction, """
                INSERT INTO dbo.portalApiUser (project_id, user_name, name_erp, display_name, password_hash, status, created_by)
                	VALUES (@p, @u, @erp, @name, @hash, '1', @by)

                SELECT CAST(SCOPE_IDENTITY() AS INT)
                """, cancellationToken,
                P("@p", SqlDbType.Int, projectId), P("@u", SqlDbType.VarChar, user.UserName, 32), P("@erp", SqlDbType.VarChar, user.NameErp, 30),
                P("@name", SqlDbType.NVarChar, Cut(user.DisplayName, 100), 100), P("@hash", SqlDbType.VarChar, user.PasswordHash, 256),
                P("@by", SqlDbType.Int, createdBy)));
            await WriteRightsAsync(connection, transaction, id, user.Forms, user.Ips, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }

        public async Task UpdateApiUserAsync(int id, string nameErp, string? displayName, bool active, IReadOnlyList<string> forms, IReadOnlyList<ApiUserIp> ips,
            CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await Db.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = await Db.BeginTransactionAsync(connection, cancellationToken);
            await Db.ExecuteAsync(connection, transaction, """
                UPDATE dbo.portalApiUser SET name_erp = @erp, display_name = @name, status = @status, sync_version = sync_version + 1 WHERE id = @id

                DELETE FROM dbo.portalApiUserForm WHERE api_user_id = @id

                DELETE FROM dbo.portalApiUserIp WHERE api_user_id = @id
                """, cancellationToken,
                P("@erp", SqlDbType.VarChar, nameErp, 30), P("@name", SqlDbType.NVarChar, Cut(displayName, 100), 100),
                P("@status", SqlDbType.Char, active ? "1" : "0", 1), P("@id", SqlDbType.Int, id));
            await WriteRightsAsync(connection, transaction, id, forms, ips, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        private async Task WriteRightsAsync(SqlConnection connection, SqlTransaction transaction, int id, IReadOnlyList<string> forms, IReadOnlyList<ApiUserIp> ips,
            CancellationToken cancellationToken)
        {
            foreach (string form in forms.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await Db.ExecuteAsync(connection, transaction, "INSERT INTO dbo.portalApiUserForm (api_user_id, form) VALUES (@id, @form)", cancellationToken,
                    P("@id", SqlDbType.Int, id), P("@form", SqlDbType.VarChar, form, 32));
            }
            foreach (ApiUserIp ip in ips.DistinctBy(i => i.Ip, StringComparer.Ordinal))
            {
                await Db.ExecuteAsync(connection, transaction, "INSERT INTO dbo.portalApiUserIp (api_user_id, ip_address, description) VALUES (@id, @ip, @d)", cancellationToken,
                    P("@id", SqlDbType.Int, id), P("@ip", SqlDbType.VarChar, ip.Ip, 45), P("@d", SqlDbType.NVarChar, Cut(ip.Description, 100), 100));
            }
        }

        public Task SetApiUserPasswordAsync(int id, string passwordHash, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalApiUser SET password_hash = @hash, password_changed_at = GETDATE() WHERE id = @id", cancellationToken,
                P("@hash", SqlDbType.VarChar, passwordHash, 256), P("@id", SqlDbType.Int, id));

        public Task MarkApiUserSyncAsync(int id, int pushedVersion, string? error, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync(error == null
                ? "UPDATE dbo.portalApiUser SET synced_version = @v, synced_at = GETDATE(), sync_error = NULL WHERE id = @id AND synced_version < @v"
                : "UPDATE dbo.portalApiUser SET sync_error = @error WHERE id = @id", cancellationToken,
                P("@v", SqlDbType.Int, pushedVersion), P("@id", SqlDbType.Int, id), P("@error", SqlDbType.NVarChar, Cut(error, 512), 512));
    }
}
