using System.Data;
using Business;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Models;
using Provider;

namespace DataAccess
{
    /// <summary>
    /// Chạy form trên SQL: tạo #errors và #khối, bulk dữ liệu, rồi trong một transaction chạy checking, processing.
    /// #errors có dòng → rollback, trả lỗi. dryRun → chạy hết checking rồi rollback, không chạy processing.
    /// Processing trả bảng 0 code khác SUCCESS → rollback (KE_HOACH 4.4)
    /// </summary>
    public class FormExecutor(AppConfig appConfig, SqlDatabase database, ILogger<FormExecutor> logger) : IFormExecutor
    {
        public async Task<FormExecutionResult> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default)
        {
            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);

            await database.ExecuteAsync(connection, "SET XACT_ABORT ON; " + CheckingCompiler.ErrorsTable, cancellationToken);
            foreach (ImportBlock block in form.Blocks)
            {
                await database.ExecuteAsync(connection, JsonBodyMapper.CreateTempTableSql(block), cancellationToken);
                DataTable table = body.Data.Tables[block.Name]!;
                if (table.Rows.Count > 0)
                {
                    await database.BulkAsync(connection, table, "#" + block.Name, cancellationToken);
                }
            }

            // Transaction bằng T-SQL: các lệnh không gắn SqlTransaction vẫn chạy trong cùng phiên
            await database.ExecuteAsync(connection, "BEGIN TRANSACTION", cancellationToken);
            try
            {
                if (form.CheckingQuery.Trim().Length > 0)
                {
                    await database.ExecuteAsync(connection, FormVariables.Replace(form.CheckingQuery, form, call, appConfig), cancellationToken);
                }

                DataSet errors = await database.RetrieveAsync(connection,
                    "SELECT block, row_no, parent_no, field, code FROM #errors ORDER BY parent_no, row_no", cancellationToken);
                List<CheckingError> checkingErrors = errors.Tables[0].Rows.Cast<DataRow>().Select(row => ToError(row, form.Root.Name)).ToList();

                if (checkingErrors.Count > 0 || call.DryRun)
                {
                    await RollbackAsync(connection);
                    return new FormExecutionResult { CheckingErrors = checkingErrors, RolledBack = true };
                }

                DataSet data = await database.RetrieveAsync(connection, FormVariables.Replace(form.ProcessingQuery, form, call, appConfig), cancellationToken);

                if (FormResultSet.CodeOf(data) != FormResultSet.Success)
                {
                    await RollbackAsync(connection);
                    return new FormExecutionResult { Data = data, RolledBack = true };
                }

                await database.ExecuteAsync(connection, "IF @@TRANCOUNT > 0 COMMIT TRANSACTION", cancellationToken);
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
                return FormResultSet.Success;
            }

            string column = form.Paging.Key;
            DataTable table = new("#transferred");
            table.Columns.Add(column, typeof(string));
            foreach (string key in keys)
            {
                table.Rows.Add(key);
            }

            await using SqlConnection connection = await database.OpenConnectionAsync(cancellationToken);
            await database.ExecuteAsync(connection,
                $"SET XACT_ABORT ON; CREATE TABLE #transferred ([{column}] NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY)", cancellationToken);
            await database.BulkAsync(connection, table, "#transferred", cancellationToken);

            await database.ExecuteAsync(connection, "BEGIN TRANSACTION", cancellationToken);
            try
            {
                DataSet result = await database.RetrieveAsync(connection, FormVariables.Replace(form.TransferredQuery, form, call, appConfig), cancellationToken);
                string code = FormResultSet.CodeOf(result);
                if (code != FormResultSet.Success)
                {
                    await RollbackAsync(connection);
                    return code;
                }
                await database.ExecuteAsync(connection, "IF @@TRANCOUNT > 0 COMMIT TRANSACTION", cancellationToken);
                return code;
            }
            catch
            {
                await RollbackAsync(connection);
                throw;
            }
        }

        private async Task RollbackAsync(SqlConnection connection)
        {
            try
            {
                // Không truyền CancellationToken: client ngắt kết nối vẫn phải rollback
                await database.ExecuteAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION");
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
}
