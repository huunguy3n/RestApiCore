using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Provider;

namespace WebPortal.Portal
{
    /// <summary>Phiên đăng nhập, challenge thay phiên, license, nhật ký</summary>
    public sealed partial class SqlPortalStore
    {
        private const string SessionColumns = "a.id, a.account_type, a.account_id, a.project_id, a.created_at, a.last_seen_at, a.ip_address, a.user_agent, a.revoked_at, a.revoked_reason";

        private static SessionRecord MapSession(DataRow r) => new((Guid)r["id"], r["account_type"].ToString()!.Trim(), Convert.ToInt32(r["account_id"]),
            r["project_id"] == DBNull.Value ? null : Convert.ToInt32(r["project_id"]), (DateTime)r["created_at"], (DateTime)r["last_seen_at"],
            r["ip_address"] as string, r["user_agent"] as string, Date(r["revoked_at"]), r["revoked_reason"] as string);

        public async Task<SessionRecord?> FindSessionAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {SessionColumns} FROM dbo.portalSession a WHERE a.id = @id", cancellationToken, P("@id", SqlDbType.UniqueIdentifier, id));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapSession).FirstOrDefault();
        }

        public async Task<SessionRecord?> FindActiveSessionAsync(string accountType, int accountId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync($"SELECT {SessionColumns} FROM dbo.portalSession a WHERE a.account_type = @t AND a.account_id = @a AND a.revoked_at IS NULL",
                cancellationToken, P("@t", SqlDbType.VarChar, accountType, 8), P("@a", SqlDbType.Int, accountId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapSession).FirstOrDefault();
        }

        private static SqlParameter[] SessionParameters(SessionRecord s) =>
        [
            P("@id", SqlDbType.UniqueIdentifier, s.Id),
            P("@t", SqlDbType.VarChar, s.AccountType, 8),
            P("@a", SqlDbType.Int, s.AccountId),
            P("@p", SqlDbType.Int, s.ProjectId),
            P("@ip", SqlDbType.VarChar, Cut(s.IpAddress, 45), 45),
            P("@ua", SqlDbType.NVarChar, Cut(s.UserAgent, 256), 256)
        ];

        private const string InsertSession = """
            INSERT INTO dbo.portalSession (id, account_type, account_id, project_id, created_at, last_seen_at, ip_address, user_agent)
            	VALUES (@id, @t, @a, @p, GETDATE(), GETDATE(), @ip, @ua)
            """;

        public async Task<bool> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default)
        {
            try
            {
                await Db.ExecuteAsync(InsertSession, cancellationToken, SessionParameters(session));
                return true;
            }
            catch (DatabaseException ex) when (ex.InnerException is SqlException sql && sql.Number is 2601 or 2627)
            {
                // UX_portalSession_active: tài khoản vừa có phiên khác (đăng nhập đồng thời)
                return false;
            }
        }

        public async Task<bool> ReplaceSessionAsync(Guid oldSessionId, SessionRecord session, string reason, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await Db.OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = await Db.BeginTransactionAsync(connection, cancellationToken);
            try
            {
                await Db.ExecuteAsync(connection, transaction, """
                    UPDATE dbo.portalSession SET revoked_at = GETDATE(), revoked_reason = @reason, replaced_by = @new
                    	WHERE account_type = @t AND account_id = @a AND revoked_at IS NULL
                    """, cancellationToken,
                    P("@reason", SqlDbType.VarChar, reason, 32), P("@new", SqlDbType.UniqueIdentifier, session.Id),
                    P("@t", SqlDbType.VarChar, session.AccountType, 8), P("@a", SqlDbType.Int, session.AccountId));
                await Db.ExecuteAsync(connection, transaction, InsertSession, cancellationToken, SessionParameters(session));
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (DatabaseException ex) when (ex.InnerException is SqlException sql && sql.Number is 2601 or 2627)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
        }

        public Task TouchSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalSession SET last_seen_at = GETDATE() WHERE id = @id AND revoked_at IS NULL", cancellationToken, P("@id", SqlDbType.UniqueIdentifier, id));

        public Task RevokeSessionAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalSession SET revoked_at = GETDATE(), revoked_reason = @reason WHERE id = @id AND revoked_at IS NULL", cancellationToken,
                P("@id", SqlDbType.UniqueIdentifier, id), P("@reason", SqlDbType.VarChar, reason, 32));

        public Task RevokeAccountSessionsAsync(string accountType, int accountId, string reason, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("UPDATE dbo.portalSession SET revoked_at = GETDATE(), revoked_reason = @reason WHERE account_type = @t AND account_id = @a AND revoked_at IS NULL",
                cancellationToken, P("@t", SqlDbType.VarChar, accountType, 8), P("@a", SqlDbType.Int, accountId), P("@reason", SqlDbType.VarChar, reason, 32));

        private static ChallengeRecord MapChallenge(DataRow r) => new((Guid)r["id"], r["account_type"].ToString()!.Trim(), Convert.ToInt32(r["account_id"]),
            r["project_id"] == DBNull.Value ? null : Convert.ToInt32(r["project_id"]), (Guid)r["old_session_id"], r["ip_address"] as string, r["user_agent"] as string,
            (DateTime)r["created_at"], (DateTime)r["expires_at"], Date(r["closed_at"]), r["outcome"] as string);

        public Task CreateChallengeAsync(ChallengeRecord c, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("""
                INSERT INTO dbo.portalLoginChallenge (id, account_type, account_id, project_id, old_session_id, ip_address, user_agent, created_at, expires_at)
                	VALUES (@id, @t, @a, @p, @old, @ip, @ua, @created, @expires)
                """, cancellationToken,
                P("@id", SqlDbType.UniqueIdentifier, c.Id), P("@t", SqlDbType.VarChar, c.AccountType, 8), P("@a", SqlDbType.Int, c.AccountId), P("@p", SqlDbType.Int, c.ProjectId),
                P("@old", SqlDbType.UniqueIdentifier, c.OldSessionId), P("@ip", SqlDbType.VarChar, Cut(c.IpAddress, 45), 45), P("@ua", SqlDbType.NVarChar, Cut(c.UserAgent, 256), 256),
                P("@created", SqlDbType.DateTime, c.CreatedAt), P("@expires", SqlDbType.DateTime, c.ExpiresAt));

        public async Task<ChallengeRecord?> FindChallengeAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("SELECT a.* FROM dbo.portalLoginChallenge a WHERE a.id = @id", cancellationToken, P("@id", SqlDbType.UniqueIdentifier, id));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapChallenge).FirstOrDefault();
        }

        public async Task<ChallengeRecord?> FindOpenChallengeForSessionAsync(Guid oldSessionId, DateTime now, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                SELECT TOP 1 a.* FROM dbo.portalLoginChallenge a
                	WHERE a.old_session_id = @old AND a.closed_at IS NULL AND a.expires_at > @now
                	ORDER BY a.created_at DESC
                """, cancellationToken, P("@old", SqlDbType.UniqueIdentifier, oldSessionId), P("@now", SqlDbType.DateTime, now));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(MapChallenge).FirstOrDefault();
        }

        public async Task<bool> CloseChallengeAsync(Guid id, string outcome, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                UPDATE dbo.portalLoginChallenge SET closed_at = GETDATE(), outcome = @outcome WHERE id = @id AND closed_at IS NULL

                SELECT @@ROWCOUNT
                """, cancellationToken, P("@id", SqlDbType.UniqueIdentifier, id), P("@outcome", SqlDbType.VarChar, outcome, 16));
            return Convert.ToInt32(ds.Tables[^1].Rows[0][0]) == 1;
        }

        // ---------- License ----------
        public Task AddLicenseAsync(LicenseRecord l, string requestSha256, int issuedBy, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("""
                INSERT INTO dbo.portalLicense (id, project_id, kid, mac_list, machine_name, request_sha256, issued_by, issued_at, expires_at)
                	VALUES (@id, @p, @kid, @macs, @machine, @sha, @by, @at, @expires)
                """, cancellationToken,
                P("@id", SqlDbType.UniqueIdentifier, l.Id), P("@p", SqlDbType.Int, l.ProjectId), P("@kid", SqlDbType.VarChar, l.Kid, 64), P("@macs", SqlDbType.VarChar, Cut(l.MacList, 512), 512),
                P("@machine", SqlDbType.NVarChar, Cut(l.MachineName, 64), 64), P("@sha", SqlDbType.Char, requestSha256, 64), P("@by", SqlDbType.Int, issuedBy),
                P("@at", SqlDbType.DateTime, l.IssuedAt), P("@expires", SqlDbType.DateTime, l.ExpiresAt));

        public async Task<IReadOnlyList<LicenseRecord>> ListLicensesAsync(int? projectId, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                SELECT TOP 500 a.id, a.project_id, a.kid, a.mac_list, a.machine_name, a.issued_at, b.login AS issued_login, a.expires_at, a.revoked_at, a.revoked_reason
                	FROM dbo.portalLicense a
                		LEFT JOIN dbo.portalAccount b ON b.id = a.issued_by
                	WHERE @p IS NULL OR a.project_id = @p
                	ORDER BY a.issued_at DESC
                """, cancellationToken, P("@p", SqlDbType.Int, projectId));
            return ds.Tables[0].Rows.Cast<DataRow>().Select(r => new LicenseRecord((Guid)r["id"], Convert.ToInt32(r["project_id"]), r["kid"].ToString()!.Trim(),
                r["mac_list"].ToString()!, r["machine_name"] as string, (DateTime)r["issued_at"], r["issued_login"] as string, Date(r["expires_at"]),
                Date(r["revoked_at"]), r["revoked_reason"] as string)).ToList();
        }

        public async Task<bool> RevokeLicenseAsync(Guid id, int? projectId, string reason, CancellationToken cancellationToken = default)
        {
            DataSet ds = await Db.RetrieveAsync("""
                UPDATE dbo.portalLicense SET revoked_at = GETDATE(), revoked_reason = @reason
                	WHERE id = @id AND revoked_at IS NULL AND (@p IS NULL OR project_id = @p)

                SELECT @@ROWCOUNT
                """, cancellationToken, P("@id", SqlDbType.UniqueIdentifier, id), P("@p", SqlDbType.Int, projectId), P("@reason", SqlDbType.NVarChar, Cut(reason, 256), 256));
            return Convert.ToInt32(ds.Tables[^1].Rows[0][0]) == 1;
        }

        // ---------- Nhật ký ----------
        public Task WriteAuditAsync(AuditRecord r, CancellationToken cancellationToken = default) =>
            Db.ExecuteAsync("""
                INSERT INTO dbo.portalAudit (actor_account_id, actor_type, actor_login, project_id, action, target, outcome, detail, ip_address, acting_as, session_id)
                	VALUES (@actor, @type, @login, @p, @action, @target, @outcome, @detail, @ip, @as, @sid)
                """, cancellationToken,
                P("@actor", SqlDbType.Int, r.ActorId), P("@type", SqlDbType.VarChar, r.ActorType, 8), P("@login", SqlDbType.VarChar, Cut(r.ActorLogin, 64), 64),
                P("@p", SqlDbType.Int, r.ProjectId), P("@action", SqlDbType.VarChar, Cut(r.Action, 64), 64), P("@target", SqlDbType.VarChar, Cut(r.Target, 128), 128),
                P("@outcome", SqlDbType.VarChar, Cut(r.Outcome, 16), 16), P("@detail", SqlDbType.NVarChar, Cut(r.Detail, 1024), 1024),
                P("@ip", SqlDbType.VarChar, Cut(r.IpAddress, 45), 45), P("@as", SqlDbType.VarChar, r.ActingAs, 8), P("@sid", SqlDbType.UniqueIdentifier, r.SessionId));

        public async Task<AuditPage> ListAuditAsync(AuditQuery q, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            const string where = """
                a.created_at >= @from AND a.created_at < @to AND (@p IS NULL OR a.project_id = @p)
                	AND (@actor IS NULL OR a.actor_login = @actor) AND (@action IS NULL OR a.action LIKE @action + '%') AND (@outcome IS NULL OR a.outcome = @outcome)
                """;
            int first = (page - 1) * pageSize + 1;
            DataSet ds = await Db.RetrieveAsync($"""
                SELECT COUNT(1) FROM dbo.portalAudit a WHERE {where}

                SELECT t.* FROM
                	(
                		SELECT a.id, a.created_at, a.actor_type, a.actor_login, a.project_id, b.ma_da, a.action, a.target, a.outcome, a.detail, a.ip_address, a.acting_as
                			 , ROW_NUMBER() OVER (ORDER BY a.created_at DESC, a.id DESC) AS rn
                			FROM dbo.portalAudit a
                				LEFT JOIN dbo.portalProject b ON b.id = a.project_id
                			WHERE {where}
                	) t
                	WHERE t.rn BETWEEN @first AND @last
                	ORDER BY t.rn
                OPTION (RECOMPILE)
                """, cancellationToken,
                P("@from", SqlDbType.DateTime, q.From), P("@to", SqlDbType.DateTime, q.To), P("@p", SqlDbType.Int, q.ProjectId),
                P("@actor", SqlDbType.VarChar, q.Actor, 64), P("@action", SqlDbType.VarChar, q.ActionPrefix, 64), P("@outcome", SqlDbType.VarChar, q.Outcome, 16),
                P("@first", SqlDbType.Int, first), P("@last", SqlDbType.Int, first + pageSize - 1));
            return new AuditPage(Convert.ToInt32(ds.Tables[0].Rows[0][0]), ds.Tables[1].Rows.Cast<DataRow>().Select(r => new AuditEntryView(Convert.ToInt64(r["id"]),
                (DateTime)r["created_at"], r["actor_type"] as string, r["actor_login"] as string, r["project_id"] == DBNull.Value ? null : Convert.ToInt32(r["project_id"]),
                (r["ma_da"] as string)?.Trim(), r["action"].ToString()!, r["target"] as string, r["outcome"].ToString()!, r["detail"] as string, r["ip_address"] as string,
                r["acting_as"] as string)).ToList());
        }

        // ---------- Đồng bộ ----------
        public async Task<SyncLogPage> ListSyncLogAsync(int projectId, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            int first = (page - 1) * pageSize + 1;
            DataSet ds = await Db.RetrieveAsync("""
                SELECT COUNT(1) FROM dbo.portalSyncLog a WHERE a.project_id = @p

                SELECT t.* FROM
                	(
                		SELECT a.id, a.created_at, a.env, a.event, a.package_id, a.source, a.actor, a.outcome, a.detail
                			 , ROW_NUMBER() OVER (ORDER BY a.created_at DESC, a.id DESC) AS rn
                			FROM dbo.portalSyncLog a
                			WHERE a.project_id = @p
                	) t
                	WHERE t.rn BETWEEN @first AND @last
                	ORDER BY t.rn
                """, cancellationToken, P("@p", SqlDbType.Int, projectId), P("@first", SqlDbType.Int, first), P("@last", SqlDbType.Int, first + pageSize - 1));
            return new SyncLogPage(Convert.ToInt32(ds.Tables[0].Rows[0][0]), ds.Tables[1].Rows.Cast<DataRow>().Select(r => new SyncLogEntry(Convert.ToInt64(r["id"]),
                (DateTime)r["created_at"], r["env"].ToString()!.Trim(), r["event"].ToString()!.Trim(), r["package_id"] == DBNull.Value ? null : Convert.ToInt32(r["package_id"]),
                r["source"].ToString()!.Trim(), r["actor"] as string, r["outcome"].ToString()!.Trim(), r["detail"] as string)).ToList());
        }

        internal static string Ids(IEnumerable<int> ids) => string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
    }
}
