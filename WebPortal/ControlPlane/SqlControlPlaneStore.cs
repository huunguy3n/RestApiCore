using System.Data;
using Microsoft.Data.SqlClient;
using Provider;

namespace WebPortal.ControlPlane
{
    /// <summary>DB portal riêng (sql/portal/001–003). Mọi giá trị đi bằng tham số; SQL Server 2008</summary>
    public sealed partial class SqlControlPlaneStore(SqlDatabase database) : IControlPlaneStore
    {
        public async Task<PortalAccount?> FindAccountByLoginAsync(string login, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT id, login, display_name, kind, status, password_hash FROM portalAccount WHERE login = @login", cancellationToken,
                new SqlParameter("@login", SqlDbType.VarChar, 64) { Value = login });
            return ds.Tables[0].Rows.Count == 0 ? null : Map(ds.Tables[0].Rows[0]);
        }

        public async Task<PortalAccount?> FindAccountAsync(int id, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT id, login, display_name, kind, status, password_hash FROM portalAccount WHERE id = @id", cancellationToken,
                new SqlParameter("@id", SqlDbType.Int) { Value = id });
            return ds.Tables[0].Rows.Count == 0 ? null : Map(ds.Tables[0].Rows[0]);
        }

        public async Task<IReadOnlyList<MembershipRow>> GetMembershipsAsync(int accountId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT m.id, m.account_id, m.project_id, r.code AS role_code, r.scope AS role_scope, m.project_user_name
                FROM portalMembership m JOIN portalRole r ON r.id = m.role_id WHERE m.account_id = @id
                """, cancellationToken, new SqlParameter("@id", SqlDbType.Int) { Value = accountId });
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new MembershipRow(Convert.ToInt32(r["id"]), Convert.ToInt32(r["account_id"]),
                r["project_id"] == DBNull.Value ? null : Convert.ToInt32(r["project_id"]), r["role_code"].ToString()!.Trim(), r["role_scope"].ToString()!.Trim(),
                r["project_user_name"] as string)).ToList();
        }

        public async Task<IReadOnlyList<ProjectInfo>> GetActiveProjectsAsync(CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT id, code, name FROM portalProject WHERE status = '1' ORDER BY code", cancellationToken);
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new ProjectInfo(Convert.ToInt32(r["id"]), r["code"].ToString()!.Trim(), r["name"].ToString()!)).ToList();
        }

        public async Task<IReadOnlyList<string>> GetRolePermissionsAsync(string roleCode, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("SELECT p.permission FROM portalRolePermission p JOIN portalRole r ON r.id = p.role_id WHERE r.code = @code", cancellationToken,
                new SqlParameter("@code", SqlDbType.VarChar, 32) { Value = roleCode });
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => r["permission"].ToString()!.Trim()).ToList();
        }

        public async Task WriteAuditAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            await database.ExecuteAsync("""
                INSERT INTO portalAudit (actor_account_id, project_id, action, target, outcome, detail)
                VALUES (@actor, @project, @action, @target, @outcome, @detail)
                """, cancellationToken,
                new SqlParameter("@actor", SqlDbType.Int) { Value = (object?)record.ActorAccountId ?? DBNull.Value },
                new SqlParameter("@project", SqlDbType.Int) { Value = (object?)record.ProjectId ?? DBNull.Value },
                new SqlParameter("@action", SqlDbType.VarChar, 64) { Value = Cut(record.Action, 64) },
                new SqlParameter("@target", SqlDbType.VarChar, 128) { Value = (object?)Cut(record.Target, 128) ?? DBNull.Value },
                new SqlParameter("@outcome", SqlDbType.VarChar, 16) { Value = Cut(record.Outcome, 16) },
                new SqlParameter("@detail", SqlDbType.NVarChar, 1024) { Value = (object?)Cut(record.Detail, 1024) ?? DBNull.Value });
        }

        /// <summary>Bỏ ký tự điều khiển (xuống dòng, v.v.) rồi cắt: chuỗi do người dùng / CLI gửi lên không làm giả được dòng nhật ký</summary>
        private static string? Cut(string? value, int max)
        {
            string? clean = value == null ? null : new string(value.Where(c => !char.IsControl(c)).ToArray());
            return clean != null && clean.Length > max ? clean[..max] : clean;
        }

        private static PortalAccount Map(DataRow r) => new(Convert.ToInt32(r["id"]), r["login"].ToString()!.Trim(), r["display_name"].ToString()!, r["kind"].ToString()!.Trim(),
            r["status"].ToString()!.Trim() == "1", r["password_hash"].ToString()!);
    }

    /// <summary>Chưa khai ControlPlane:ConnectionString: mọi truy vấn báo chưa cấu hình, trang Admin hiện thông báo thay vì lỗi 500</summary>
    public sealed partial class UnconfiguredControlPlaneStore : IControlPlaneStore
    {
        private static InvalidOperationException Fail() => new("Chưa khai ControlPlane:ConnectionString");

        public Task<PortalAccount?> FindAccountByLoginAsync(string login, CancellationToken cancellationToken = default) => throw Fail();
        public Task<PortalAccount?> FindAccountAsync(int id, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<MembershipRow>> GetMembershipsAsync(int accountId, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<ProjectInfo>> GetActiveProjectsAsync(CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<string>> GetRolePermissionsAsync(string roleCode, CancellationToken cancellationToken = default) => throw Fail();
        public Task WriteAuditAsync(AuditRecord record, CancellationToken cancellationToken = default) => throw Fail();
    }
}
