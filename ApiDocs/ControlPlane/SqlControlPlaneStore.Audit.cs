using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace ApiDocs.ControlPlane
{
    /// <summary>Truy vấn chỉ đọc cho màn hình audit và dashboard. Mọi giá trị người dùng đi bằng tham số; SQL Server 2008</summary>
    public sealed partial class SqlControlPlaneStore
    {
        /// <summary>projectIds là int do server tính nên ghép vào IN là an toàn; null = không giới hạn, rỗng = không dòng nào</summary>
        private static string ProjectScope(string column, IReadOnlyCollection<int>? projectIds) =>
            projectIds == null ? "1 = 1"
            : projectIds.Count == 0 ? "1 = 0"
            : $"{column} IN (" + string.Join(",", projectIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")";

        private static string AccountScope(IReadOnlyCollection<int>? projectIds) =>
            projectIds == null ? "1 = 1"
            : projectIds.Count == 0 ? "1 = 0"
            : "EXISTS (SELECT 1 FROM portalMembership m WHERE m.account_id = a.id AND m.project_id IN (" + string.Join(",", projectIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "))";

        public async Task<AuditPage> ListAuditAsync(AuditQuery query, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default)
        {
            string where = $"""
                a.created_at >= @from_time AND a.created_at < @to_time
                AND (@project IS NULL OR a.project_id = @project)
                AND (@person = N'' OR c.login LIKE N'%' + @person + N'%' ESCAPE N'\' OR a.target LIKE N'%' + @person + N'%' ESCAPE N'\')
                AND (@action = N'' OR a.action LIKE @action + N'%' ESCAPE N'\')
                AND (@outcome = N'' OR a.outcome = @outcome)
                AND {ProjectScope("a.project_id", projectIds)}
                """;
            const string from = "FROM portalAudit a LEFT JOIN portalAccount c ON c.id = a.actor_account_id LEFT JOIN portalProject p ON p.id = a.project_id";
            int first = (page - 1) * pageSize + 1;

            DataSet ds = await database.RetrieveAsync($"""
                SELECT COUNT(1) {from} WHERE {where}
                SELECT t.* FROM
                (
                    SELECT a.id, a.created_at, a.actor_account_id, c.login AS actor_login, a.project_id, p.code AS project_code, a.action, a.target, a.outcome, a.detail,
                        ROW_NUMBER() OVER (ORDER BY a.created_at DESC, a.id DESC) AS rn
                    {from} WHERE {where}
                ) t WHERE t.rn BETWEEN @rn_from AND @rn_to ORDER BY t.rn
                """, cancellationToken,
                new SqlParameter("@from_time", SqlDbType.DateTime) { Value = query.From },
                new SqlParameter("@to_time", SqlDbType.DateTime) { Value = query.To },
                new SqlParameter("@project", SqlDbType.Int) { Value = (object?)query.ProjectId ?? DBNull.Value },
                new SqlParameter("@person", SqlDbType.NVarChar, 200) { Value = EscapeLike(query.Person) },
                new SqlParameter("@action", SqlDbType.NVarChar, 100) { Value = EscapeLike(query.ActionPrefix) },
                new SqlParameter("@outcome", SqlDbType.VarChar, 16) { Value = query.Outcome },
                new SqlParameter("@rn_from", SqlDbType.Int) { Value = first },
                new SqlParameter("@rn_to", SqlDbType.Int) { Value = first + pageSize - 1 });

            return new AuditPage(Convert.ToInt32(ds.Tables[0].Rows[0][0]), ds.Tables[1].Rows.Cast<DataRow>().Select(r => new AuditEntry(
                Convert.ToInt64(r["id"]), (DateTime)r["created_at"], r["actor_account_id"] == DBNull.Value ? null : Convert.ToInt32(r["actor_account_id"]), r["actor_login"] as string,
                r["project_id"] == DBNull.Value ? null : Convert.ToInt32(r["project_id"]), (r["project_code"] as string)?.Trim(),
                r["action"].ToString()!.Trim(), r["target"] as string, r["outcome"].ToString()!.Trim(), r["detail"] as string)).ToList());
        }

        public async Task<AccountCounts> GetAccountCountsAsync(IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync($"""
                SELECT ISNULL(SUM(CASE WHEN a.kind = 'staff' AND a.status = '1' THEN 1 ELSE 0 END), 0) AS staff_active,
                    ISNULL(SUM(CASE WHEN a.kind = 'staff' AND a.status <> '1' THEN 1 ELSE 0 END), 0) AS staff_locked,
                    ISNULL(SUM(CASE WHEN a.kind = 'customer' AND a.status = '1' THEN 1 ELSE 0 END), 0) AS customer_active,
                    ISNULL(SUM(CASE WHEN a.kind = 'customer' AND a.status <> '1' THEN 1 ELSE 0 END), 0) AS customer_locked
                FROM portalAccount a WHERE {AccountScope(projectIds)}
                """, cancellationToken);
            DataRow row = ds.Tables[0].Rows[0];
            return new AccountCounts(Convert.ToInt32(row["staff_active"]), Convert.ToInt32(row["staff_locked"]), Convert.ToInt32(row["customer_active"]), Convert.ToInt32(row["customer_locked"]));
        }

        public async Task<IReadOnlyList<ProjectStat>> GetProjectStatsAsync(IReadOnlyCollection<int>? projectIds, DateTime deniedSince, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync($"""
                SELECT p.id, p.code, p.name, p.status,
                    (SELECT COUNT(DISTINCT a.id) FROM portalMembership m JOIN portalAccount a ON a.id = m.account_id WHERE m.project_id = p.id AND a.kind = 'customer' AND a.status = '1') AS customers,
                    (SELECT COUNT(DISTINCT a.id) FROM portalMembership m JOIN portalAccount a ON a.id = m.account_id WHERE m.project_id = p.id AND a.kind = 'staff' AND a.status = '1') AS staff,
                    (SELECT COUNT(1) FROM portalAudit x WHERE x.project_id = p.id AND x.outcome = 'DENIED' AND x.created_at >= @since) AS denied
                FROM portalProject p WHERE {ProjectScope("p.id", projectIds)} ORDER BY p.code
                """, cancellationToken, new SqlParameter("@since", SqlDbType.DateTime) { Value = deniedSince });
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new ProjectStat(Convert.ToInt32(r["id"]), r["code"].ToString()!.Trim(), r["name"].ToString()!,
                r["status"].ToString()!.Trim() == "1", Convert.ToInt32(r["customers"]), Convert.ToInt32(r["staff"]), Convert.ToInt32(r["denied"]))).ToList();
        }

        public async Task<int> CountAuditAsync(DateTime since, string outcome, string? actionPrefix, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync($"""
                SELECT COUNT(1) FROM portalAudit a
                WHERE a.created_at >= @since AND a.outcome = @outcome AND (@action = N'' OR a.action LIKE @action + N'%' ESCAPE N'\') AND {ProjectScope("a.project_id", projectIds)}
                """, cancellationToken,
                new SqlParameter("@since", SqlDbType.DateTime) { Value = since },
                new SqlParameter("@outcome", SqlDbType.VarChar, 16) { Value = outcome },
                new SqlParameter("@action", SqlDbType.NVarChar, 100) { Value = EscapeLike(actionPrefix ?? "") });
            return Convert.ToInt32(ds.Tables[0].Rows[0][0]);
        }
    }

    public sealed partial class UnconfiguredControlPlaneStore
    {
        public Task<AuditPage> ListAuditAsync(AuditQuery query, int page, int pageSize, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default) => throw Fail();
        public Task<AccountCounts> GetAccountCountsAsync(IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<ProjectStat>> GetProjectStatsAsync(IReadOnlyCollection<int>? projectIds, DateTime deniedSince, CancellationToken cancellationToken = default) => throw Fail();
        public Task<int> CountAuditAsync(DateTime since, string outcome, string? actionPrefix, IReadOnlyCollection<int>? projectIds, CancellationToken cancellationToken = default) => throw Fail();
    }
}
