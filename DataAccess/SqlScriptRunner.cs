using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Provider;

namespace DataAccess
{
    public sealed record ScriptStatus(string Script, string Sha256, string State, DateTime? AppliedAt);

    public sealed record ScriptRunResult(string Script, bool Applied, string? Error);

    /// <summary>
    /// Áp script sql/NNN_*.sql theo thứ tự số, ghi restApiSetupLog (version, script, sha256, applied_at, applied_by, duration_ms).
    /// Script đã áp mà nội dung đổi vẫn chạy lại được (mọi script restApi* đều chạy lại không lỗi), nhưng được báo "CHANGED" để người cài biết.
    /// Tách lô theo dòng GO như sqlcmd. Mỗi script một transaction; lỗi thì rollback script đó và dừng.
    /// Mọi kết nối đi qua Provider.SqlDatabase
    /// </summary>
    public static partial class SqlScriptRunner
    {
        private const string EnsureLog = """
            IF OBJECT_ID('dbo.restApiSetupLog', 'U') IS NULL
            	CREATE TABLE dbo.restApiSetupLog
            	(
            		id INT IDENTITY(1, 1) NOT NULL,
            		version VARCHAR(32) NOT NULL,
            		script VARCHAR(128) NOT NULL,
            		sha256 CHAR(64) NOT NULL,
            		applied_at DATETIME NOT NULL CONSTRAINT DF_restApiSetupLog_applied_at DEFAULT (GETDATE()),
            		applied_by NVARCHAR(128) NULL,
            		duration_ms INT NOT NULL,
            		CONSTRAINT PK_restApiSetupLog PRIMARY KEY (id)
            	)
            """;

        public static IReadOnlyList<string> FindScripts(string directory) =>
            Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.sql").Where(f => ScriptName().IsMatch(Path.GetFileName(f))).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];

        public static Task<IReadOnlyList<ScriptStatus>> StatusAsync(string connectionString, IReadOnlyList<string> scripts, CancellationToken cancellationToken = default) =>
            StatusAsync(new SqlDatabase(connectionString, 600), scripts, cancellationToken);

        public static async Task<IReadOnlyList<ScriptStatus>> StatusAsync(SqlDatabase database, IReadOnlyList<string> scripts, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await database.ExecuteAsync(connection, EnsureLog, cancellationToken);

            Dictionary<string, (string Sha, DateTime At)> applied = new(StringComparer.OrdinalIgnoreCase);
            DataSet log = await database.RetrieveAsync(connection, "SELECT a.script, a.sha256, a.applied_at FROM dbo.restApiSetupLog a ORDER BY a.id", cancellationToken);
            foreach (DataRow row in log.Tables[0].Rows)
            {
                applied[Convert.ToString(row["script"])!] = (Convert.ToString(row["sha256"])!.Trim(), Convert.ToDateTime(row["applied_at"]));
            }

            return scripts.Select(path =>
            {
                string name = Path.GetFileName(path), sha = Sha256(File.ReadAllText(path, Encoding.UTF8));
                return applied.TryGetValue(name, out var row)
                    ? new ScriptStatus(name, sha, string.Equals(row.Sha, sha, StringComparison.OrdinalIgnoreCase) ? "APPLIED" : "CHANGED", row.At)
                    : new ScriptStatus(name, sha, "PENDING", null);
            }).ToList();
        }

        /// <param name="onlyPending">true: bỏ qua script đã áp cùng nội dung</param>
        public static Task<IReadOnlyList<ScriptRunResult>> RunAsync(string connectionString, IReadOnlyList<string> scripts, string version, string appliedBy,
            bool onlyPending, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            RunAsync(new SqlDatabase(connectionString, 600), scripts, version, appliedBy, onlyPending, progress, cancellationToken);

        public static async Task<IReadOnlyList<ScriptRunResult>> RunAsync(SqlDatabase database, IReadOnlyList<string> scripts, string version, string appliedBy,
            bool onlyPending, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ScriptStatus> statuses = await StatusAsync(database, scripts, cancellationToken);
            List<ScriptRunResult> results = [];
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);

            foreach (string path in scripts)
            {
                string name = Path.GetFileName(path);
                ScriptStatus status = statuses.First(s => s.Script == name);
                if (onlyPending && status.State == "APPLIED")
                {
                    results.Add(new ScriptRunResult(name, false, null));
                    continue;
                }

                progress?.Report($"Chạy {name}…");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await using SqlTransaction transaction = await database.BeginTransactionAsync(connection, cancellationToken);
                try
                {
                    foreach (string batch in SplitBatches(File.ReadAllText(path, Encoding.UTF8)))
                    {
                        await database.ExecuteAsync(connection, transaction, batch, cancellationToken);
                    }
                    await database.ExecuteAsync(connection, transaction, """
                        INSERT INTO dbo.restApiSetupLog (version, script, sha256, applied_by, duration_ms) VALUES (@version, @script, @sha, @by, @ms)
                        """, cancellationToken,
                        new SqlParameter("@version", SqlDbType.VarChar, 32) { Value = version },
                        new SqlParameter("@script", SqlDbType.VarChar, 128) { Value = name },
                        new SqlParameter("@sha", SqlDbType.Char, 64) { Value = status.Sha256 },
                        new SqlParameter("@by", SqlDbType.NVarChar, 128) { Value = appliedBy },
                        new SqlParameter("@ms", SqlDbType.Int) { Value = (int)watch.ElapsedMilliseconds });
                    await transaction.CommitAsync(cancellationToken);
                    results.Add(new ScriptRunResult(name, true, null));
                }
                catch (DatabaseException ex) when (ex.InnerException is SqlException sql)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    results.Add(new ScriptRunResult(name, false, $"Dòng {sql.LineNumber}: {sql.Message}"));
                    break;
                }
                catch (SqlException ex)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    results.Add(new ScriptRunResult(name, false, $"Dòng {ex.LineNumber}: {ex.Message}"));
                    break;
                }
            }
            return results;
        }

        /// <summary>Tách theo dòng chỉ có "GO" (không phân biệt hoa thường), như sqlcmd</summary>
        public static IReadOnlyList<string> SplitBatches(string script) =>
            GoLine().Split(script).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();

        private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        [GeneratedRegex(@"^\d{3}_[A-Za-z0-9_]+\.sql$")]
        private static partial Regex ScriptName();

        [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
        private static partial Regex GoLine();
    }
}
