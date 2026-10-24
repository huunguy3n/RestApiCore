using Business;
using Microsoft.Data.SqlClient;
using Provider;
using System.Data;

namespace DataAccess
{
    /// <summary>
    /// Vai trò portal, danh sách user, quyền form và nhật ký quản trị. SQL Server 2008: phân trang bằng ROW_NUMBER, không OFFSET/FETCH
    /// </summary>
    public class PortalAccessRepository(SqlDatabase database) : IPortalAccessRepository
    {
        /// <summary>Lỗi "Invalid object name": chưa chạy sql/008. Coi như không có vai trò nào (deny)</summary>
        private const int InvalidObjectName = 208;

        public async Task<(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions)> GetRolesAsync(int uId, CancellationToken cancellationToken = default)
        {
            try
            {
                DataSet ds = await database.RetrieveAsync("""
                    SELECT r.code FROM restApiPortalUserRole ur JOIN restApiPortalRole r ON r.id = ur.role_id WHERE ur.u_id = @u_id
                    SELECT DISTINCT p.permission FROM restApiPortalUserRole ur JOIN restApiPortalRolePermission p ON p.role_id = ur.role_id WHERE ur.u_id = @u_id
                    """, cancellationToken, new SqlParameter("@u_id", SqlDbType.Int) { Value = uId });
                return (Column(ds.Tables[0], "code"), Column(ds.Tables[1], "permission"));
            }
            catch (DatabaseException ex) when (ex.InnerException is SqlException { Number: InvalidObjectName })
            {
                return ([], []);
            }
        }

        public async Task<PortalUserPage> ListUsersAsync(string search, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            int from = (page - 1) * pageSize + 1;
            SqlParameter[] parameters =
            [
                new("@search", SqlDbType.VarChar, 32) { Value = search },
                new("@from", SqlDbType.Int) { Value = from },
                new("@to", SqlDbType.Int) { Value = from + pageSize - 1 }
            ];

            // Không chọn password/private_key: chỉ cờ has_key
            DataSet ds = await database.RetrieveAsync("""
                SELECT COUNT(1) AS total FROM restApiUser WHERE @search = '' OR [name] LIKE '%' + @search + '%'
                SELECT t.id, t.[name], t.name_erp, t.[status], t.has_key FROM
                (
                    SELECT a.id, a.[name], a.name_erp, a.[status], CASE WHEN a.private_key IS NULL THEN 0 ELSE 1 END AS has_key,
                        ROW_NUMBER() OVER (ORDER BY a.[name]) AS rn
                    FROM restApiUser a WHERE @search = '' OR a.[name] LIKE '%' + @search + '%'
                ) t WHERE t.rn BETWEEN @from AND @to ORDER BY t.rn
                """, cancellationToken, parameters);

            int total = Convert.ToInt32(ds.Tables[0].Rows[0]["total"]);
            List<PortalUserSummary> items = [];
            foreach (DataRow row in ds.Tables[1].Rows)
            {
                int uId = Convert.ToInt32(row["id"]);
                var (roles, _) = await GetRolesAsync(uId, cancellationToken);
                items.Add(new PortalUserSummary(uId, row["name"].ToString()!.TrimEnd(), (row["name_erp"] as string ?? "").TrimEnd(),
                    (row["status"] as string ?? "").Trim() == "1", Convert.ToInt32(row["has_key"]) == 1, roles));
            }
            return new PortalUserPage(total, items);
        }

        public async Task<UserFormAccess?> GetUserFormsAsync(string userName, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT a.id, a.[name], ISNULL(b.admin, 0) AS admin FROM restApiUser a LEFT JOIN vsysuserinfo b ON b.[name] = a.name_erp WHERE a.[name] = @name
                SELECT f.form FROM restApiAllowForm f JOIN restApiUser a ON a.id = f.u_id WHERE a.[name] = @name ORDER BY f.form
                """, cancellationToken, new SqlParameter("@name", SqlDbType.VarChar, 32) { Value = userName });

            if (ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }
            DataRow row = ds.Tables[0].Rows[0];
            return new UserFormAccess(Convert.ToInt32(row["id"]), row["name"].ToString()!.TrimEnd(), Convert.ToInt32(row["admin"]) == 1, Column(ds.Tables[1], "form"));
        }

        public async Task ReplaceUserFormsAsync(int uId, IReadOnlyCollection<string> forms, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await Run(connection, transaction, "DELETE FROM restApiAllowForm WHERE u_id = @u_id", uId, null, cancellationToken);
                foreach (string form in forms)
                {
                    await Run(connection, transaction, "INSERT INTO restApiAllowForm (u_id, form) VALUES (@u_id, @form)", uId, form, cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task WriteAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            await database.ExecuteAsync("""
                INSERT INTO restApiAudit (actor_u_id, actor_name, action, target_type, target, outcome, req_id, detail)
                VALUES (@actor_u_id, @actor_name, @action, @target_type, @target, @outcome, @req_id, @detail)
                """, cancellationToken,
                new SqlParameter("@actor_u_id", SqlDbType.Int) { Value = (object?)auditEvent.ActorUId ?? DBNull.Value },
                new SqlParameter("@actor_name", SqlDbType.VarChar, 32) { Value = Trim(auditEvent.ActorName, 32) },
                new SqlParameter("@action", SqlDbType.VarChar, 64) { Value = Trim(auditEvent.Action, 64) },
                new SqlParameter("@target_type", SqlDbType.VarChar, 32) { Value = Trim(auditEvent.TargetType, 32) },
                new SqlParameter("@target", SqlDbType.VarChar, 128) { Value = Trim(auditEvent.Target, 128) },
                new SqlParameter("@outcome", SqlDbType.VarChar, 16) { Value = Trim(auditEvent.Outcome, 16) },
                new SqlParameter("@req_id", SqlDbType.VarChar, 64) { Value = (object?)auditEvent.RequestId ?? DBNull.Value },
                new SqlParameter("@detail", SqlDbType.NVarChar, 1024) { Value = (object?)(auditEvent.Detail == null ? null : Trim(auditEvent.Detail, 1024)) ?? DBNull.Value });
        }

        private static string Trim(string value, int max) => value.Length > max ? value[..max] : value;

        private static IReadOnlyList<string> Column(DataTable table, string name) => table.Rows.Cast<DataRow>().Select(r => r[name].ToString()!.Trim()).ToList();

        private static async Task Run(SqlConnection connection, SqlTransaction transaction, string text, int uId, string? form, CancellationToken cancellationToken)
        {
            await using SqlCommand command = new(text, connection, transaction);
            command.Parameters.Add(new SqlParameter("@u_id", SqlDbType.Int) { Value = uId });
            if (form != null)
            {
                command.Parameters.Add(new SqlParameter("@form", SqlDbType.VarChar, 32) { Value = form });
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
