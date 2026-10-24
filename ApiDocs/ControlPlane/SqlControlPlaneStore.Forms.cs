using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace ApiDocs.ControlPlane
{
    /// <summary>Revision form, package và nhật ký đồng bộ (bảng ở sql/portal/002). Mọi giá trị đi bằng tham số; SQL Server 2008</summary>
    public sealed partial class SqlControlPlaneStore
    {
        private static SqlParameter P(string name, SqlDbType type, object? value, int size = 0) =>
            size > 0 ? new SqlParameter(name, type, size) { Value = value ?? DBNull.Value } : new SqlParameter(name, type) { Value = value ?? DBNull.Value };

        public async Task<IReadOnlyList<FormSummary>> ListFormsAsync(int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT r.form_name, MAX(r.revision) AS latest,
                    (SELECT TOP 1 x.status FROM portalFormRevision x WHERE x.project_id = @p AND x.form_name = r.form_name ORDER BY x.revision DESC) AS latest_status,
                    (SELECT MAX(x.revision) FROM portalFormRevision x WHERE x.project_id = @p AND x.form_name = r.form_name AND x.status = 'published') AS active_revision,
                    (SELECT TOP 1 x.created_at FROM portalFormRevision x WHERE x.project_id = @p AND x.form_name = r.form_name ORDER BY x.revision DESC) AS updated_at,
                    (SELECT TOP 1 c.login FROM portalFormRevision x LEFT JOIN portalAccount c ON c.id = x.created_by WHERE x.project_id = @p AND x.form_name = r.form_name ORDER BY x.revision DESC) AS updated_by
                FROM portalFormRevision r WHERE r.project_id = @p GROUP BY r.form_name ORDER BY r.form_name
                """, cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new FormSummary(r["form_name"].ToString()!.Trim(), Convert.ToInt32(r["latest"]), r["latest_status"].ToString()!.Trim(),
                r["active_revision"] == DBNull.Value ? null : Convert.ToInt32(r["active_revision"]), (DateTime)r["updated_at"], r["updated_by"] as string)).ToList();
        }

        private const string RevisionColumns = """
            r.id, r.revision, r.base_revision, r.status, r.content_sha256, r.created_at, c.login AS created_login, r.published_at, p.login AS published_login
            """;
        private const string RevisionFrom = "FROM portalFormRevision r LEFT JOIN portalAccount c ON c.id = r.created_by LEFT JOIN portalAccount p ON p.id = r.published_by";

        private static RevisionInfo MapRevision(DataRow r) => new(Convert.ToInt32(r["id"]), Convert.ToInt32(r["revision"]), r["base_revision"] == DBNull.Value ? null : Convert.ToInt32(r["base_revision"]),
            r["status"].ToString()!.Trim(), r["content_sha256"].ToString()!.Trim(), (DateTime)r["created_at"], r["created_login"] as string,
            r["published_at"] == DBNull.Value ? null : (DateTime)r["published_at"], r["published_login"] as string);

        public async Task<IReadOnlyList<RevisionInfo>> GetRevisionsAsync(int projectId, string form, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync($"SELECT {RevisionColumns} {RevisionFrom} WHERE r.project_id = @p AND r.form_name = @f ORDER BY r.revision DESC", cancellationToken,
                P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapRevision).ToList();
        }

        public async Task<RevisionContent?> GetRevisionAsync(int projectId, string form, int revision, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync($"SELECT {RevisionColumns}, r.content {RevisionFrom} WHERE r.project_id = @p AND r.form_name = @f AND r.revision = @v", cancellationToken,
                P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32), P("@v", SqlDbType.Int, revision));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new RevisionContent(MapRevision(r), r["content"].ToString()!)).FirstOrDefault();
        }

        public async Task<int?> AddRevisionAsync(int projectId, string form, int? baseRevision, string content, string sha256, string status, int createdBy, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            // Serializable + khóa theo (project, form): hai người lưu cùng lúc thì một người thấy revision lớn nhất đã đổi và nhận CONFLICT
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            int? current;
            await using (SqlCommand max = new("SELECT MAX(revision) FROM portalFormRevision WITH (UPDLOCK, HOLDLOCK) WHERE project_id = @p AND form_name = @f", connection, transaction))
            {
                max.Parameters.Add(P("@p", SqlDbType.Int, projectId));
                max.Parameters.Add(P("@f", SqlDbType.VarChar, form, 32));
                object? value = await max.ExecuteScalarAsync(cancellationToken);
                current = value == null || value == DBNull.Value ? null : Convert.ToInt32(value);
            }
            if (current != baseRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }

            int next = (current ?? 0) + 1;
            await using (SqlCommand insert = new("""
                INSERT INTO portalFormRevision (project_id, form_name, revision, base_revision, status, content, content_sha256, created_by)
                VALUES (@p, @f, @v, @base, @status, @content, @sha, @by)
                """, connection, transaction))
            {
                insert.Parameters.Add(P("@p", SqlDbType.Int, projectId));
                insert.Parameters.Add(P("@f", SqlDbType.VarChar, form, 32));
                insert.Parameters.Add(P("@v", SqlDbType.Int, next));
                insert.Parameters.Add(P("@base", SqlDbType.Int, baseRevision));
                insert.Parameters.Add(P("@status", SqlDbType.VarChar, status, 10));
                insert.Parameters.Add(P("@content", SqlDbType.NVarChar, content, -1));
                insert.Parameters.Add(P("@sha", SqlDbType.Char, sha256, 64));
                insert.Parameters.Add(P("@by", SqlDbType.Int, createdBy));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return next;
        }

        public async Task<bool> PublishRevisionAsync(int projectId, string form, int revision, int publishedBy, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                UPDATE portalFormRevision SET status = 'published', published_by = @by, published_at = GETDATE()
                WHERE project_id = @p AND form_name = @f AND revision = @v AND status = 'validated';
                SELECT @@ROWCOUNT AS n;
                """, cancellationToken, P("@by", SqlDbType.Int, publishedBy), P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32), P("@v", SqlDbType.Int, revision));
            return Convert.ToInt32(ds.Tables[^1].Rows[0][0]) == 1;
        }

        public async Task<IReadOnlyList<ActiveForm>> GetActiveFormsAsync(int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync("""
                SELECT r.id, r.form_name, r.revision, r.content FROM portalFormRevision r
                WHERE r.project_id = @p AND r.status = 'published'
                    AND r.revision = (SELECT MAX(x.revision) FROM portalFormRevision x WHERE x.project_id = r.project_id AND x.form_name = r.form_name AND x.status = 'published')
                ORDER BY r.form_name
                """, cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new ActiveForm(r["form_name"].ToString()!.Trim(), Convert.ToInt32(r["id"]), Convert.ToInt32(r["revision"]), r["content"].ToString()!)).ToList();
        }

        public async Task<int> CreatePackageAsync(int projectId, int createdBy, Func<int, PackageDraft> build, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                int id;
                await using (SqlCommand insert = new("""
                    INSERT INTO portalPackage (project_id, created_by, manifest, manifest_sha256) VALUES (@p, @by, N'{}', REPLICATE('0', 64));
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """, connection, transaction))
                {
                    insert.Parameters.Add(P("@p", SqlDbType.Int, projectId));
                    insert.Parameters.Add(P("@by", SqlDbType.Int, createdBy));
                    id = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
                }

                PackageDraft draft = build(id);
                await using (SqlCommand update = new("UPDATE portalPackage SET manifest = @m, manifest_sha256 = @s WHERE id = @id", connection, transaction))
                {
                    update.Parameters.Add(P("@m", SqlDbType.NVarChar, draft.ManifestJson, -1));
                    update.Parameters.Add(P("@s", SqlDbType.Char, draft.ManifestSha256, 64));
                    update.Parameters.Add(P("@id", SqlDbType.Int, id));
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }
                foreach (int revisionId in draft.RevisionIds)
                {
                    await using SqlCommand item = new("INSERT INTO portalPackageItem (package_id, revision_id) VALUES (@pk, @r)", connection, transaction);
                    item.Parameters.Add(P("@pk", SqlDbType.Int, id));
                    item.Parameters.Add(P("@r", SqlDbType.Int, revisionId));
                    await item.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return id;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        private const string PackageColumns = """
            k.id, k.project_id, k.created_at, c.login AS created_login, k.manifest_sha256, (SELECT COUNT(1) FROM portalPackageItem i WHERE i.package_id = k.id) AS form_count
            """;

        private static PackageInfo MapPackage(DataRow r) => new(Convert.ToInt32(r["id"]), Convert.ToInt32(r["project_id"]), (DateTime)r["created_at"], r["created_login"] as string,
            r["manifest_sha256"].ToString()!.Trim(), Convert.ToInt32(r["form_count"]));

        public async Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync($"SELECT TOP 100 {PackageColumns} FROM portalPackage k LEFT JOIN portalAccount c ON c.id = k.created_by WHERE k.project_id = @p ORDER BY k.id DESC",
                cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapPackage).ToList();
        }

        public async Task<PackageRecord?> GetPackageAsync(int projectId, int packageId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await database.RetrieveAsync($"""
                SELECT {PackageColumns}, k.manifest FROM portalPackage k LEFT JOIN portalAccount c ON c.id = k.created_by WHERE k.project_id = @p AND k.id = @id
                SELECT r.form_name, r.revision, r.content FROM portalPackageItem i JOIN portalFormRevision r ON r.id = i.revision_id WHERE i.package_id = @id ORDER BY r.form_name
                """, cancellationToken, P("@p", SqlDbType.Int, projectId), P("@id", SqlDbType.Int, packageId));
            if (ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }
            DataRow row = ds.Tables[0].Rows[0];
            return new PackageRecord(MapPackage(row), row["manifest"].ToString()!,
                ds.Tables[1].Rows.Cast<DataRow>().Select(r => new PackageItem(r["form_name"].ToString()!.Trim(), Convert.ToInt32(r["revision"]), r["content"].ToString()!)).ToList());
        }

        public Task AddSyncLogAsync(SyncLogRecord record, CancellationToken cancellationToken = default) =>
            database.ExecuteAsync("""
                INSERT INTO portalSyncLog (project_id, env, event, package_id, source, actor, outcome, detail) VALUES (@p, @env, @event, @pk, @source, @actor, @outcome, @detail)
                """, cancellationToken,
                P("@p", SqlDbType.Int, record.ProjectId), P("@env", SqlDbType.VarChar, record.Env, 4), P("@event", SqlDbType.VarChar, record.Event, 32), P("@pk", SqlDbType.Int, record.PackageId),
                P("@source", SqlDbType.VarChar, record.Source, 8), P("@actor", SqlDbType.VarChar, Cut(record.Actor, 64), 64),
                P("@outcome", SqlDbType.VarChar, record.Outcome, 8), P("@detail", SqlDbType.NVarChar, Cut(record.Detail, 1024), 1024));

        public async Task<SyncLogPage> ListSyncLogAsync(IReadOnlyCollection<int> projectIds, int? projectId, string env, string outcome, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            if (projectIds.Count == 0)
            {
                return new SyncLogPage(0, []);
            }
            string scope = "s.project_id IN (" + string.Join(",", projectIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")";
            string where = $"{scope} AND (@project IS NULL OR s.project_id = @project) AND (@env = '' OR s.env = @env) AND (@outcome = '' OR s.outcome = @outcome)";
            int first = (page - 1) * pageSize + 1;
            DataSet ds = await database.RetrieveAsync($"""
                SELECT COUNT(1) FROM portalSyncLog s WHERE {where}
                SELECT t.* FROM
                (
                    SELECT s.id, s.created_at, s.project_id, p.code AS project_code, s.env, s.event, s.package_id, s.source, s.actor, s.outcome, s.detail,
                        ROW_NUMBER() OVER (ORDER BY s.created_at DESC, s.id DESC) AS rn
                    FROM portalSyncLog s JOIN portalProject p ON p.id = s.project_id WHERE {where}
                ) t WHERE t.rn BETWEEN @from AND @to ORDER BY t.rn
                """, cancellationToken,
                P("@project", SqlDbType.Int, projectId), P("@env", SqlDbType.VarChar, env, 4), P("@outcome", SqlDbType.VarChar, outcome, 8),
                P("@from", SqlDbType.Int, first), P("@to", SqlDbType.Int, first + pageSize - 1));
            return new SyncLogPage(Convert.ToInt32(ds.Tables[0].Rows[0][0]), ds.Tables[1].Rows.Cast<DataRow>().Select(r => new SyncLogEntry(Convert.ToInt64(r["id"]), (DateTime)r["created_at"],
                Convert.ToInt32(r["project_id"]), r["project_code"].ToString()!.Trim(), r["env"].ToString()!.Trim(), r["event"].ToString()!.Trim(),
                r["package_id"] == DBNull.Value ? null : Convert.ToInt32(r["package_id"]), r["source"].ToString()!.Trim(), r["actor"] as string, r["outcome"].ToString()!.Trim(), r["detail"] as string)).ToList());
        }
    }

    public sealed partial class UnconfiguredControlPlaneStore
    {
        public Task<IReadOnlyList<FormSummary>> ListFormsAsync(int projectId, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<RevisionInfo>> GetRevisionsAsync(int projectId, string form, CancellationToken cancellationToken = default) => throw Fail();
        public Task<RevisionContent?> GetRevisionAsync(int projectId, string form, int revision, CancellationToken cancellationToken = default) => throw Fail();
        public Task<int?> AddRevisionAsync(int projectId, string form, int? baseRevision, string content, string sha256, string status, int createdBy, CancellationToken cancellationToken = default) => throw Fail();
        public Task<bool> PublishRevisionAsync(int projectId, string form, int revision, int publishedBy, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<ActiveForm>> GetActiveFormsAsync(int projectId, CancellationToken cancellationToken = default) => throw Fail();
        public Task<int> CreatePackageAsync(int projectId, int createdBy, Func<int, PackageDraft> build, CancellationToken cancellationToken = default) => throw Fail();
        public Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(int projectId, CancellationToken cancellationToken = default) => throw Fail();
        public Task<PackageRecord?> GetPackageAsync(int projectId, int packageId, CancellationToken cancellationToken = default) => throw Fail();
        public Task AddSyncLogAsync(SyncLogRecord record, CancellationToken cancellationToken = default) => throw Fail();
        public Task<SyncLogPage> ListSyncLogAsync(IReadOnlyCollection<int> projectIds, int? projectId, string env, string outcome, int page, int pageSize, CancellationToken cancellationToken = default) => throw Fail();
    }
}
