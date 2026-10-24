using System.Data;
using System.Text.RegularExpressions;
using Models;

namespace Business
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

    /// <summary>
    /// Cổng chạy form trên SQL (KE_HOACH 4.4): bảng tạm theo khối, checking, processing trong một transaction.
    /// Cài đặt: DataAccess.FormExecutor
    /// </summary>
    public interface IFormExecutor
    {
        Task<FormExecutionResult> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default);

        /// <summary>
        /// Form Transfer: chạy query transferred với #transferred([key]) = key của trang vừa trả. Trả code (SUCCESS hoặc mã lỗi của query)
        /// </summary>
        Task<string> MarkTransferredAsync(ImportForm form, IReadOnlyList<string> keys, FormCallContext call, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Thay biến @@ trong query (KE_HOACH 2.6). Chuỗi thành literal N'…' đã escape, số giữ nguyên, tên DB thành [tên].
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
