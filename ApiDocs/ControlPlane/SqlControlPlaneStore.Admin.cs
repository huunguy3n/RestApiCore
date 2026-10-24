using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace ApiDocs.ControlPlane
{
    /// <summary>Quản trị tài khoản / membership / dự án. Mọi giá trị đi bằng tham số; SQL Server 2008</summary>
    public sealed partial class SqlControlPlaneStore
    {
        private const int DuplicateKey = 2627, DuplicateIndex = 2601;

        private static bool IsDuplicate(Exception ex) => (ex as SqlException ?? ex.InnerException as SqlException)?.Number is DuplicateKey or DuplicateIndex;

        /// <summary>Ký tự đặc biệt của LIKE bị escape để chuỗi tìm kiếm không thành mẫu tùy ý (ESCAPE dùng dấu \)</summary>
        public static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

        public async Task<AccountPage> ListAccountsAsync(string search, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default)
        {
            // projectIds là int do server tính nên ghép vào IN là an toàn; rỗng thì không tài khoản nào
            string scope = projectIds == null ? "1 = 1"
                : projectIds.Count == 0 ? "1 = 0"
                : "EXISTS (SELECT 1 FROM portalMembership m WHERE m.account_id = a.id AND m.project_id IN (" + string.Join(",", projectIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "))";
            const string like = "(@search = N'' OR a.login LIKE N'%' + @search + N'%' ESCAPE N'\\' OR a.display_name LIKE N'%' + @search + N'%' ESCAPE N'\\')";
            int first = (page - 1) * pageSize + 1;

            DataSet ds = await database.RetrieveAsync($"""
                SELECT COUNT(1) FROM portalAccount a WHERE {like} AND {scope}
                SELECT t.id, t.login, t.display_name, t.kind, t.status FROM
                (
                    SELECT a.id, a.login, a.display_name, a.kind, a.status, ROW_NUMBER() OVER (ORDER BY a.login) AS rn
                    FROM portalAccount a WHERE {like} AND {scope}
                ) t WHERE t.rn BETWEEN @from AND @to ORDER BY t.rn
                """, cancellationToken,
                new SqlParameter("@search", SqlDbType.NVarChar, 200) { Value = EscapeLike(search) },
                new SqlParameter("@from", SqlDbType.Int) { Value = first },
                new SqlParameter("@to", SqlDbType.Int) { Value = first + pageSize - 1 });

            return new AccountPage(Convert.ToInt32(ds.Tables[0].Rows[0][0]), ds.Tables[1].Rows.Cast<DataRow>()
                .Select(r => new AccountSummary(Convert.ToInt32(r["id"]), r["login"].ToString()!.Trim(), r["display_name"].ToString()!, r["kind"].ToString()!.Trim(), r["status"].ToString()!.Trim() == "1")).ToList());
        }

        public async Task<int?> CreateAccountAsync(string login, string displayName, string kind, string passwordHash, NewMembership first, int? grantedBy, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                int id;
                await using (SqlCommand insert = new("""
                    INSERT INTO portalAccount (login, display_name, kind, status, password_hash) VALUES (@login, @name, @kind, '1', @hash);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """, connection, transaction))
                {
                    insert.Parameters.Add(new SqlParameter("@login", SqlDbType.VarChar, 64) { Value = login });
                    insert.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 100) { Value = displayName });
                    insert.Parameters.Add(new SqlParameter("@kind", SqlDbType.VarChar, 10) { Value = kind });
                    insert.Parameters.Add(new SqlParameter("@hash", SqlDbType.VarChar, 256) { Value = passwordHash });
                    id = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
                }
                if (!await InsertMembershipAsync(connection, transaction, id, first, grantedBy, cancellationToken))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return null;
                }
                await transaction.CommitAsync(cancellationToken);
                return id;
            }
            catch (SqlException ex) when (IsDuplicate(ex))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
        }

        private static async Task<bool> InsertMembershipAsync(SqlConnection connection, SqlTransaction? transaction, int accountId, NewMembership m, int? grantedBy, CancellationToken cancellationToken)
        {
            await using SqlCommand command = new("""
                INSERT INTO portalMembership (account_id, project_id, role_id, project_user_name, granted_by)
                SELECT @account, @project, r.id, @user, @by FROM portalRole r WHERE r.code = @role
                """, connection, transaction);
            command.Parameters.Add(new SqlParameter("@account", SqlDbType.Int) { Value = accountId });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.Int) { Value = (object?)m.ProjectId ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@user", SqlDbType.VarChar, 32) { Value = (object?)m.ProjectUserName ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@by", SqlDbType.Int) { Value = (object?)grantedBy ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@role", SqlDbType.VarChar, 32) { Value = m.RoleCode });
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        /// <summary>Số administrator trung tâm đang hoạt động khác tài khoản / membership bị loại trừ; đọc có khóa để giữ nguyên đến hết transaction</summary>
        private static async Task<int> CountOtherCentralAdministratorsAsync(SqlConnection connection, SqlTransaction transaction, int? excludeAccountId, int? excludeMembershipId, CancellationToken cancellationToken)
        {
            await using SqlCommand command = new("""
                SELECT COUNT(DISTINCT a.id) FROM portalAccount a WITH (UPDLOCK, HOLDLOCK)
                JOIN portalMembership m WITH (UPDLOCK, HOLDLOCK) ON m.account_id = a.id AND m.project_id IS NULL
                JOIN portalRole r ON r.id = m.role_id AND r.code = 'administrator' AND r.scope = 'staff'
                WHERE a.kind = 'staff' AND a.status = '1' AND (@exclude_account IS NULL OR a.id <> @exclude_account) AND (@exclude_membership IS NULL OR m.id <> @exclude_membership)
                """, connection, transaction);
            command.Parameters.Add(new SqlParameter("@exclude_account", SqlDbType.Int) { Value = (object?)excludeAccountId ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@exclude_membership", SqlDbType.Int) { Value = (object?)excludeMembershipId ?? DBNull.Value });
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        public async Task<bool> SetAccountActiveAsync(int id, bool active, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (keepOneCentralAdministrator && await CountOtherCentralAdministratorsAsync(connection, transaction, id, null, cancellationToken) == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
            await using (SqlCommand update = new("UPDATE portalAccount SET status = @status WHERE id = @id", connection, transaction))
            {
                update.Parameters.Add(new SqlParameter("@status", SqlDbType.Char, 1) { Value = active ? "1" : "0" });
                update.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        public Task UpdatePasswordHashAsync(int id, string passwordHash, CancellationToken cancellationToken = default) =>
            database.ExecuteAsync("UPDATE portalAccount SET password_hash = @hash WHERE id = @id", cancellationToken,
                new SqlParameter("@hash", SqlDbType.VarChar, 256) { Value = passwordHash }, new SqlParameter("@id", SqlDbType.Int) { Value = id });

        public async Task<MembershipRow?> GetMembershipAsync(int membershipId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT m.id, m.account_id, m.project_id, r.code AS role_code, r.scope AS role_scope, m.project_user_name
                FROM portalMembership m JOIN portalRole r ON r.id = m.role_id WHERE m.id = @id
                """, cancellationToken, new SqlParameter("@id", SqlDbType.Int) { Value = membershipId });
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new MembershipRow(Convert.ToInt32(r["id"]), Convert.ToInt32(r["account_id"]),
                r["project_id"] == DBNull.Value ? null : Convert.ToInt32(r["project_id"]), r["role_code"].ToString()!.Trim(), r["role_scope"].ToString()!.Trim(),
                r["project_user_name"] as string)).FirstOrDefault();
        }

        public async Task<bool> AddMembershipAsync(int accountId, NewMembership membership, int? grantedBy, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            try
            {
                return await InsertMembershipAsync(connection, null, accountId, membership, grantedBy, cancellationToken);
            }
            catch (SqlException ex) when (IsDuplicate(ex))
            {
                return false;
            }
        }

        public async Task<bool> RemoveMembershipAsync(int membershipId, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (keepOneCentralAdministrator && await CountOtherCentralAdministratorsAsync(connection, transaction, null, membershipId, cancellationToken) == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
            await using (SqlCommand delete = new("DELETE FROM portalMembership WHERE id = @id", connection, transaction))
            {
                delete.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = membershipId });
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        public async Task<IReadOnlyList<ProjectDetail>> GetAllProjectsAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT id, code, name, api_base_url, status FROM portalProject ORDER BY code", cancellationToken);
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new ProjectDetail(Convert.ToInt32(r["id"]), r["code"].ToString()!.Trim(), r["name"].ToString()!,
                r["api_base_url"].ToString()!, r["status"].ToString()!.Trim() == "1")).ToList();
        }

        public async Task<int?> CreateProjectAsync(string code, string name, string apiBaseUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                DataSet ds = await database.RetrieveAsync("""
                    INSERT INTO portalProject (code, name, api_base_url, status) VALUES (@code, @name, @url, '1');
                    SELECT CAST(SCOPE_IDENTITY() AS INT) AS id;
                    """, cancellationToken,
                    new SqlParameter("@code", SqlDbType.VarChar, 32) { Value = code },
                    new SqlParameter("@name", SqlDbType.NVarChar, 100) { Value = name },
                    new SqlParameter("@url", SqlDbType.VarChar, 256) { Value = apiBaseUrl });
                return Convert.ToInt32(ds.Tables[^1].Rows[0][0]);
            }
            catch (Exception ex) when (IsDuplicate(ex))
            {
                return null;
            }
        }

        public async Task<bool> UpdateProjectAsync(int id, string name, string apiBaseUrl, bool active, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                UPDATE portalProject SET name = @name, api_base_url = @url, status = @status WHERE id = @id;
                SELECT @@ROWCOUNT AS n;
                """, cancellationToken,
                new SqlParameter("@name", SqlDbType.NVarChar, 100) { Value = name },
                new SqlParameter("@url", SqlDbType.VarChar, 256) { Value = apiBaseUrl },
                new SqlParameter("@status", SqlDbType.Char, 1) { Value = active ? "1" : "0" },
                new SqlParameter("@id", SqlDbType.Int) { Value = id });
            return Convert.ToInt32(ds.Tables[^1].Rows[0][0]) == 1;
        }
    }

    public sealed partial class UnconfiguredControlPlaneStore
    {
        public Task<AccountPage> ListAccountsAsync(string search, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default) => throw Fail();
        public Task<int?> CreateAccountAsync(string login, string displayName, string kind, string passwordHash, NewMembership first, int? grantedBy, CancellationToken cancellationToken = default) => throw Fail();
        public Task<bool> SetAccountActiveAsync(int id, bool active, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default) => throw Fail();
        public Task UpdatePasswordHashAsync(int id, string passwordHash, CancellationToken cancellationToken = default) => throw Fail();
        public Task<MembershipRow?> GetMembershipAsync(int membershipId, CancellationToken cancellationToken = default) => throw Fail();
        public Task<bool> AddMembershipAsync(int accountId, NewMembership membership, int? grantedBy, CancellationToken cancellationToken = default) => throw Fail();
        public Task<bool> RemoveMembershipAsync(int membershipId, bool keepOneCentralAdministrator, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<ProjectDetail>> GetAllProjectsAsync(CancellationToken cancellationToken = default) => throw Fail();
        public Task<int?> CreateProjectAsync(string code, string name, string apiBaseUrl, CancellationToken cancellationToken = default) => throw Fail();
        public Task<bool> UpdateProjectAsync(int id, string name, string apiBaseUrl, bool active, CancellationToken cancellationToken = default) => throw Fail();
    }
}
