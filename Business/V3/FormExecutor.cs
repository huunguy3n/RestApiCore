using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Models;
using Models.V3;

namespace Business.V3
{
    /// <summary>
    /// Thông tin request đã qua xác thực và kiểm quyền, dùng để thay biến @@ trong query
    /// </summary>
    public sealed record FormCallContext(int UId, string UserName, int UserId, string UserNameErp, bool Admin, string Unit, bool DryRun);

    /// <summary>Lỗi checking: một dòng #errors đã đổi sang vị trí JSON</summary>
    public sealed record CheckingError(string Position, string Block, string Field, string Code);

    public sealed class FormExecutionResult
    {
        /// <summary>Có dòng #errors: dừng sau checking, đã rollback</summary>
        public IReadOnlyList<CheckingError> CheckingErrors { get; init; } = [];
        /// <summary>Dataset của query processing (rỗng khi dryRun hoặc có lỗi checking)</summary>
        public DataSet? Data { get; init; }
        public bool RolledBack { get; init; }
    }

    public interface IFormExecutor
    {
        Task<FormExecutionResult> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default);

        /// <summary>
        /// Form Transfer: chạy query transferred với #transferred([key]) = key của trang vừa trả. Trả code (SUCCESS hoặc mã lỗi của query)
        /// </summary>
        Task<string> MarkTransferredAsync(ImportForm form, IReadOnlyList<string> keys, FormCallContext call, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Chạy form trên SQL: tạo #errors và #khối, bulk dữ liệu, rồi trong một transaction chạy checking, processing.
    /// #errors có dòng → rollback, trả lỗi. dryRun → chạy hết checking rồi rollback, không chạy processing.
    /// Processing trả bảng 0 code khác SUCCESS → rollback (KE_HOACH_V3 4.4)
    /// </summary>
    public class FormExecutor(AppConfig appConfig, ILogger<FormExecutor> logger) : IFormExecutor
    {
        public async Task<FormExecutionResult> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await Provider.Provider.OpenConnAsync(appConfig.appConnectionString, cancellationToken);

            await Provider.Provider.ExecuteAsync(connection, "SET XACT_ABORT ON; " + CheckingCompiler.ErrorsTable, cancellationToken);
            foreach (ImportBlock block in form.Blocks)
            {
                await Provider.Provider.ExecuteAsync(connection, JsonBodyMapper.CreateTempTableSql(block), cancellationToken);
                DataTable table = body.Data.Tables[block.Name]!;
                if (table.Rows.Count > 0)
                {
                    await Provider.Provider.BulkAsync(connection, table, "#" + block.Name, cancellationToken);
                }
            }

            // Transaction bằng T-SQL: các lệnh Provider không gắn SqlTransaction vẫn chạy trong cùng phiên
            await Provider.Provider.ExecuteAsync(connection, "BEGIN TRANSACTION", cancellationToken);
            try
            {
                if (form.CheckingQuery.Trim().Length > 0)
                {
                    await Provider.Provider.ExecuteAsync(connection, FormVariables.Replace(form.CheckingQuery, form, call, appConfig), cancellationToken);
                }

                DataSet errors = await Provider.Provider.RetrieveAsync(connection,
                    "SELECT block, row_no, parent_no, field, code FROM #errors ORDER BY parent_no, row_no", cancellationToken);
                List<CheckingError> checkingErrors = errors.Tables[0].Rows.Cast<DataRow>().Select(row => ToError(row, form.Root.Name)).ToList();

                if (checkingErrors.Count > 0 || call.DryRun)
                {
                    await RollbackAsync(connection);
                    return new FormExecutionResult { CheckingErrors = checkingErrors, RolledBack = true };
                }

                DataSet data = await Provider.Provider.RetrieveAsync(connection, FormVariables.Replace(form.ProcessingQuery, form, call, appConfig), cancellationToken);

                if (ResultCode(data) != "SUCCESS")
                {
                    await RollbackAsync(connection);
                    return new FormExecutionResult { Data = data, RolledBack = true };
                }

                await Provider.Provider.ExecuteAsync(connection, "IF @@TRANCOUNT > 0 COMMIT TRANSACTION", cancellationToken);
                return new FormExecutionResult { Data = data };
            }
            catch
            {
                await RollbackAsync(connection);
                throw;
            }
        }

        public async Task<string> MarkTransferredAsync(ImportForm form, IReadOnlyList<string> keys, FormCallContext call, CancellationToken cancellationToken = default)
        {
            if (keys.Count == 0 || form.Paging == null)
            {
                return "SUCCESS";
            }

            string column = form.Paging.Key;
            DataTable table = new("#transferred");
            table.Columns.Add(column, typeof(string));
            foreach (string key in keys)
            {
                table.Rows.Add(key);
            }

            await using SqlConnection connection = await Provider.Provider.OpenConnAsync(appConfig.appConnectionString, cancellationToken);
            await Provider.Provider.ExecuteAsync(connection,
                $"SET XACT_ABORT ON; CREATE TABLE #transferred ([{column}] NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY)", cancellationToken);
            await Provider.Provider.BulkAsync(connection, table, "#transferred", cancellationToken);

            await Provider.Provider.ExecuteAsync(connection, "BEGIN TRANSACTION", cancellationToken);
            try
            {
                DataSet result = await Provider.Provider.RetrieveAsync(connection, FormVariables.Replace(form.TransferredQuery, form, call, appConfig), cancellationToken);
                string code = ResultCode(result);
                if (code != "SUCCESS")
                {
                    await RollbackAsync(connection);
                    return code;
                }
                await Provider.Provider.ExecuteAsync(connection, "IF @@TRANCOUNT > 0 COMMIT TRANSACTION", cancellationToken);
                return code;
            }
            catch
            {
                await RollbackAsync(connection);
                throw;
            }
        }

        /// <summary>Bảng 0 cột code; không có thì coi là SUCCESS</summary>
        public static string ResultCode(DataSet data) =>
            data.Tables.Count > 0 && data.Tables[0].Columns.Contains("code") && data.Tables[0].Rows.Count > 0
                ? data.Tables[0].Rows[0]["code"]?.ToString()?.Trim() ?? "SUCCESS"
                : "SUCCESS";

        private async Task RollbackAsync(SqlConnection connection)
        {
            try
            {
                // Không truyền CancellationToken: client ngắt kết nối vẫn phải rollback
                await Provider.Provider.ExecuteAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rollback thất bại");
            }
        }

        /// <summary>
        /// Vị trí như JsonBodyMapper: khối gốc [chứng từ].khối.field, khối con [chứng từ].khối[dòng].field
        /// </summary>
        public static CheckingError ToError(DataRow row, string rootBlock)
        {
            string block = row["block"].ToString()!.Trim();
            string field = row["field"].ToString()!.Trim();
            int parentNo = Convert.ToInt32(row["parent_no"]);
            string position = string.Equals(block, rootBlock, StringComparison.OrdinalIgnoreCase)
                ? $"[{parentNo}].{block}.{field}"
                : $"[{parentNo}].{block}[{Convert.ToInt32(row["row_no"])}].{field}";
            return new CheckingError(position, block, field, row["code"].ToString()!.Trim());
        }
    }

    /// <summary>
    /// Thay biến @@ trong query (KE_HOACH_V3 2.6). Chuỗi thành literal N'…' đã escape, số giữ nguyên, tên DB thành [tên].
    /// @@unit chỉ có giá trị sau khi unitCode đã qua restApi_CheckUnitRights
    /// </summary>
    public static partial class FormVariables
    {
        [GeneratedRegex(@"@@([A-Za-z]+)\b")]
        private static partial Regex Token();

        public static string Replace(string query, ImportForm form, FormCallContext call, AppConfig appConfig)
        {
            List<ImportField> keys = form.Root.Fields.Where(f => f.IsPrimaryKey).ToList();

            return Token().Replace(query, match => match.Groups[1].Value.ToLowerInvariant() switch
            {
                "form" => Literal(form.Name),
                "resource" => Literal(form.Resource),
                "verb" => Literal(form.Verb),
                "type" => Literal(form.Type.ToString()),
                "vouchercode" => Literal(form.VoucherCode),
                "userid" => call.UserId.ToString(),
                "username" => Literal(call.UserNameErp.TrimEnd()),
                "apiusername" => Literal(call.UserName),
                "admin" => call.Admin ? "1" : "0",
                "unit" => Literal(call.Unit),
                "appdatabasename" => Identifier(appConfig.appDatabaseName),
                "sysdatabasename" => Identifier(appConfig.sysDatabaseName),
                "primarykeylist" => string.Join(", ", keys.Select(k => k.FastName)),
                "primarykeylistalias" => string.Join(", ", keys.Select(k => $"{k.FastName} AS {k.Name}")),
                _ => match.Value
            });
        }

        public static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

        private static string Identifier(string value) => "[" + value.Replace("]", "]]") + "]";
    }
}
