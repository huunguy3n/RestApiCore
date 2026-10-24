using System.Data;
using Microsoft.Data.SqlClient;

namespace WebPortal.Portal
{
    /// <summary>Revision form, package và nhật ký đồng bộ (bảng ở sql/portal/002). Mọi giá trị đi bằng tham số; SQL Server 2008</summary>
    public sealed partial class SqlPortalStore
    {
        public async Task<IReadOnlyList<FormSummary>> ListFormsAsync(int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                SELECT r.form_name, MAX(r.revision) AS latest,
                	(SELECT TOP 1 x.status FROM dbo.portalFormRevision x WHERE x.project_id = @p AND x.form_name = r.form_name ORDER BY x.revision DESC) AS latest_status,
                	(SELECT MAX(x.revision) FROM dbo.portalFormRevision x WHERE x.project_id = @p AND x.form_name = r.form_name AND x.status = 'published') AS active_revision,
                	(SELECT TOP 1 x.created_at FROM dbo.portalFormRevision x WHERE x.project_id = @p AND x.form_name = r.form_name ORDER BY x.revision DESC) AS updated_at,
                	(SELECT TOP 1 c.login FROM dbo.portalFormRevision x LEFT JOIN dbo.portalAccount c ON c.id = x.created_by
                		WHERE x.project_id = @p AND x.form_name = r.form_name ORDER BY x.revision DESC) AS updated_by
                	FROM dbo.portalFormRevision r WHERE r.project_id = @p GROUP BY r.form_name ORDER BY r.form_name
                """, cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new FormSummary(r["form_name"].ToString()!.Trim(), Convert.ToInt32(r["latest"]), r["latest_status"].ToString()!.Trim(),
                r["active_revision"] == DBNull.Value ? null : Convert.ToInt32(r["active_revision"]), (DateTime)r["updated_at"], r["updated_by"] as string)).ToList();
        }

        private const string RevisionColumns = """
            r.id, r.revision, r.base_revision, r.status, r.content_sha256, r.created_at, c.login AS created_login, r.published_at, p.login AS published_login
            """;
        private const string RevisionFrom = "FROM dbo.portalFormRevision r LEFT JOIN dbo.portalAccount c ON c.id = r.created_by LEFT JOIN dbo.portalAccount p ON p.id = r.published_by";

        private static RevisionInfo MapRevision(DataRow r) => new(Convert.ToInt32(r["id"]), Convert.ToInt32(r["revision"]), r["base_revision"] == DBNull.Value ? null : Convert.ToInt32(r["base_revision"]),
            r["status"].ToString()!.Trim(), r["content_sha256"].ToString()!.Trim(), (DateTime)r["created_at"], r["created_login"] as string,
            r["published_at"] == DBNull.Value ? null : (DateTime)r["published_at"], r["published_login"] as string);

        public async Task<IReadOnlyList<RevisionInfo>> GetRevisionsAsync(int projectId, string form, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {RevisionColumns} {RevisionFrom} WHERE r.project_id = @p AND r.form_name = @f ORDER BY r.revision DESC", cancellationToken,
                P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapRevision).ToList();
        }

        public async Task<RevisionContent?> GetRevisionAsync(int projectId, string form, int revision, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {RevisionColumns}, r.content {RevisionFrom} WHERE r.project_id = @p AND r.form_name = @f AND r.revision = @v", cancellationToken,
                P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32), P("@v", SqlDbType.Int, revision));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new RevisionContent(MapRevision(r), r["content"].ToString()!)).FirstOrDefault();
        }

        public async Task<int?> AddRevisionAsync(int projectId, string form, int? baseRevision, string content, string sha256, string status, int createdBy, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await Db.OpenConnectionAsync(cancellationToken);
            // Serializable + khóa theo (project, form): hai người lưu cùng lúc thì một người thấy revision lớn nhất đã đổi và nhận CONFLICT
            await using SqlTransaction transaction = await Db.BeginTransactionAsync(connection, IsolationLevel.Serializable, cancellationToken);
            object? value = await Db.ExecuteScalarAsync(connection, transaction,
                "SELECT MAX(revision) FROM dbo.portalFormRevision WITH (UPDLOCK, HOLDLOCK) WHERE project_id = @p AND form_name = @f",
                cancellationToken, P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32));
            int? current = value == null ? null : Convert.ToInt32(value);
            if (current != baseRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }

            int next = (current ?? 0) + 1;
            await Db.ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.portalFormRevision (project_id, form_name, revision, base_revision, status, content, content_sha256, created_by)
                	VALUES (@p, @f, @v, @base, @status, @content, @sha, @by)
                """, cancellationToken,
                P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32), P("@v", SqlDbType.Int, next),
                P("@base", SqlDbType.Int, baseRevision), P("@status", SqlDbType.VarChar, status, 10),
                P("@content", SqlDbType.NVarChar, content, -1), P("@sha", SqlDbType.Char, sha256, 64), P("@by", SqlDbType.Int, createdBy));
            await transaction.CommitAsync(cancellationToken);
            return next;
        }

        public async Task<bool> PublishRevisionAsync(int projectId, string form, int revision, int publishedBy, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                UPDATE dbo.portalFormRevision SET status = 'published', published_by = @by, published_at = GETDATE()
                	WHERE project_id = @p AND form_name = @f AND revision = @v AND status = 'validated'

                SELECT @@ROWCOUNT AS n
                """, cancellationToken, P("@by", SqlDbType.Int, publishedBy), P("@p", SqlDbType.Int, projectId), P("@f", SqlDbType.VarChar, form, 32), P("@v", SqlDbType.Int, revision));
            return Convert.ToInt32(ds.Tables[^1].Rows[0][0]) == 1;
        }

        public async Task<IReadOnlyList<ActiveForm>> GetActiveFormsAsync(int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                SELECT r.id, r.form_name, r.revision, r.content FROM dbo.portalFormRevision r
                	WHERE r.project_id = @p AND r.status = 'published'
                		AND r.revision = (SELECT MAX(x.revision) FROM dbo.portalFormRevision x WHERE x.project_id = r.project_id AND x.form_name = r.form_name AND x.status = 'published')
                	ORDER BY r.form_name
                """, cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new ActiveForm(r["form_name"].ToString()!.Trim(), Convert.ToInt32(r["id"]), Convert.ToInt32(r["revision"]), r["content"].ToString()!)).ToList();
        }

        public async Task<int> CreatePackageAsync(int projectId, int createdBy, Func<int, PackageDraft> build, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await Db.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = await Db.BeginTransactionAsync(connection, cancellationToken);
            try
            {
                int id = Convert.ToInt32(await Db.ExecuteScalarAsync(connection, transaction, """
                    INSERT INTO dbo.portalPackage (project_id, created_by, manifest, manifest_sha256) VALUES (@p, @by, N'{}', REPLICATE('0', 64))

                    SELECT CAST(SCOPE_IDENTITY() AS INT)
                    """, cancellationToken, P("@p", SqlDbType.Int, projectId), P("@by", SqlDbType.Int, createdBy)));

                PackageDraft draft = build(id);
                await Db.ExecuteAsync(connection, transaction, "UPDATE dbo.portalPackage SET manifest = @m, manifest_sha256 = @s WHERE id = @id", cancellationToken,
                    P("@m", SqlDbType.NVarChar, draft.ManifestJson, -1), P("@s", SqlDbType.Char, draft.ManifestSha256, 64), P("@id", SqlDbType.Int, id));
                foreach (int revisionId in draft.RevisionIds)
                {
                    await Db.ExecuteAsync(connection, transaction, "INSERT INTO dbo.portalPackageItem (package_id, revision_id) VALUES (@pk, @r)", cancellationToken,
                        P("@pk", SqlDbType.Int, id), P("@r", SqlDbType.Int, revisionId));
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
            k.id, k.project_id, k.created_at, c.login AS created_login, k.manifest_sha256, (SELECT COUNT(1) FROM dbo.portalPackageItem i WHERE i.package_id = k.id) AS form_count
            """;

        private static PackageInfo MapPackage(DataRow r) => new(Convert.ToInt32(r["id"]), Convert.ToInt32(r["project_id"]), (DateTime)r["created_at"], r["created_login"] as string,
            r["manifest_sha256"].ToString()!.Trim(), Convert.ToInt32(r["form_count"]));

        public async Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(int projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT TOP 100 {PackageColumns} FROM dbo.portalPackage k LEFT JOIN dbo.portalAccount c ON c.id = k.created_by WHERE k.project_id = @p ORDER BY k.id DESC",
                cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapPackage).ToList();
        }

        public async Task<PackageRecord?> GetPackageAsync(int projectId, int packageId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"""
                SELECT {PackageColumns}, k.manifest FROM dbo.portalPackage k LEFT JOIN dbo.portalAccount c ON c.id = k.created_by WHERE k.project_id = @p AND k.id = @id

                SELECT r.form_name, r.revision, r.content FROM dbo.portalPackageItem i JOIN dbo.portalFormRevision r ON r.id = i.revision_id WHERE i.package_id = @id ORDER BY r.form_name
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
            Db.ExecuteAsync("""
                INSERT INTO dbo.portalSyncLog (project_id, env, event, package_id, source, actor, outcome, detail) VALUES (@p, @env, @event, @pk, @source, @actor, @outcome, @detail)
                """, cancellationToken,
                P("@p", SqlDbType.Int, record.ProjectId), P("@env", SqlDbType.VarChar, record.Env, 4), P("@event", SqlDbType.VarChar, record.Event, 32), P("@pk", SqlDbType.Int, record.PackageId),
                P("@source", SqlDbType.VarChar, record.Source, 8), P("@actor", SqlDbType.VarChar, Cut(record.Actor, 64), 64),
                P("@outcome", SqlDbType.VarChar, record.Outcome, 8), P("@detail", SqlDbType.NVarChar, Cut(record.Detail, 1024), 1024));
    }
}
