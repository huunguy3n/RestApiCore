using System.Data;
using System.Text.RegularExpressions;
using Models;
using Models.Options;

namespace Business
{
    /// <summary>Kết quả xử lý một request form, chưa gắn HTTP. Tầng Api đổi sang status + JSON</summary>
    public abstract record FormOutcome;

    /// <summary>Lỗi theo mã Message.xml; Message khác null thì thay message mặc định</summary>
    public sealed record FormFailed(string Code, string? Message = null) : FormOutcome;

    /// <summary>VALIDATION_FAILED: lỗi giá trị field (JsonBodyMapper) hoặc dòng #errors của checking</summary>
    public sealed record FormInvalid(IReadOnlyList<FormFieldError> Errors) : FormOutcome;

    public sealed record FormFieldError(string Position, string Field, string Code);

    /// <summary>dryRun: đã chạy hết checking rồi rollback</summary>
    public sealed record FormDryRun(int Documents) : FormOutcome;

    /// <summary>Kết quả processing không phân trang</summary>
    public sealed record FormCompleted(string Code, string Message, IReadOnlyDictionary<string, DataTable> Tables) : FormOutcome;

    /// <summary>Một trang: DataJson là { tên bảng: [dòng…] } đã dựng sẵn (trang 0 vừa chạy hoặc trang đọc từ cursor)</summary>
    public sealed record FormPaged(string Code, string Message, string DataJson, string? Cursor, int PageIndex, int PageCount, int PageSize,
        int TotalRowCount, int? TransferredCount) : FormOutcome;

    /// <summary>
    /// Use case route nghiệp vụ v3 (KE_HOACH 4.1, 4.4) sau khi đã kiểm chữ ký: quyền form (mặc định từ chối) → quyền đơn vị
    /// → dạng body + giá trị field → chạy form → phân trang / đánh dấu Transfer. Không biết HTTP; Idempotency-Key và JSON ở FormEndpoint
    /// </summary>
    public sealed class FormRequestService(IFormExecutor executor, IUnitRightsChecker unitRights, ICursorStore cursors, Options options, AppConfig appConfig)
    {
        /// <returns>null nếu được phép; FORBIDDEN, UNIT_REQUIRED hoặc mã của restApi_CheckUnitRights</returns>
        public async Task<FormFailed?> AuthorizeAsync(ImportForm form, SignerKeys signer, string unit, CancellationToken cancellationToken = default)
        {
            if (!signer.CanAccessForm(form.Name))
            {
                return new FormFailed("FORBIDDEN");
            }
            if (form.UnitRequired && unit.Length == 0)
            {
                return new FormFailed("UNIT_REQUIRED");
            }
            if (unit.Length > 0)
            {
                string code = await unitRights.CheckAsync(signer.UserId!.Value, unit, form.UnitAction, cancellationToken);
                if (code != FormResultSet.Success)
                {
                    return new FormFailed(code);
                }
            }
            return null;
        }

        public static FormCallContext CallContext(SignerKeys signer, string unit, bool dryRun) =>
            new(signer.UId, signer.Name, signer.UserId!.Value, signer.NameErp, signer.ErpAdmin, unit, dryRun);

        /// <param name="irregular">Lớp ký tự đặc biệt (Irregular.txt)</param>
        /// <returns>Body đã map; Rejected khác null nếu sai dạng (INVALID_SHAPE) hoặc sai giá trị (VALIDATION_FAILED)</returns>
        public (MappedBody Body, FormOutcome? Rejected) MapBody(ImportForm form, byte[] rawBody, Regex? irregular)
        {
            MappedBody body = JsonBodyMapper.Map(form, rawBody, appConfig.maxDocumentsPerRequest, irregular);
            if (body.ShapeError != null)
            {
                return (body, new FormFailed("INVALID_SHAPE", body.ShapeError));
            }
            if (body.Errors.Count > 0)
            {
                return (body, new FormInvalid(body.Errors.Select(e => new FormFieldError(e.Position, e.Field, e.Code)).ToList()));
            }
            return (body, null);
        }

        public async Task<FormOutcome> ExecuteAsync(ImportForm form, MappedBody body, FormCallContext call, CancellationToken cancellationToken = default)
        {
            FormExecutionResult result = await executor.ExecuteAsync(form, body, call, cancellationToken);

            if (result.CheckingErrors.Count > 0)
            {
                return new FormInvalid(result.CheckingErrors.Select(e => new FormFieldError(e.Position, e.Field, e.Code)).ToList());
            }
            if (call.DryRun)
            {
                return new FormDryRun(body.DocumentCount);
            }
            return await CompleteAsync(form, call, FormResultSet.From(result.Data!), cancellationToken);
        }

        /// <summary>
        /// ?cursor=…&amp;page=n: trang n (từ 0) đã lưu, không chạy lại query. Cursor phải của chính user, cùng form và cùng unitCode
        /// </summary>
        public async Task<FormOutcome> NextPageAsync(ImportForm form, SignerKeys signer, string unit, string cursor, string pageText, CancellationToken cancellationToken = default)
        {
            CursorInfo? info = await cursors.GetInfoAsync(signer.UId, cursor, cancellationToken);
            if (info == null || info.Form != form.Name || !string.Equals(info.Unit, unit, StringComparison.OrdinalIgnoreCase))
            {
                return new FormFailed("CURSOR_EXPIRED");
            }

            if (!int.TryParse(pageText.Length == 0 ? "0" : pageText, out int page) || page < 0 || page >= info.PageCount)
            {
                return new FormFailed("PAGE_OUT_OF_RANGE", $"page phải từ 0 tới {info.PageCount - 1}");
            }

            string? json = await cursors.GetPageJsonAsync(signer.UId, cursor, page, cancellationToken);
            if (json == null)
            {
                return new FormFailed("CURSOR_EXPIRED");
            }

            int? transferred = null;
            if (form.Type == ImportFormType.Transfer)
            {
                IReadOnlyList<string>? keys = await cursors.GetPageKeysAsync(signer.UId, cursor, page, cancellationToken);
                if (keys == null)
                {
                    return new FormFailed("CURSOR_EXPIRED");
                }
                if (await MarkTransferredAsync(form, keys, CallContext(signer, info.Unit, false), cancellationToken) is FormFailed failed)
                {
                    return failed;
                }
                transferred = keys.Count;
            }

            return new FormPaged(FormResultSet.Success, "", json, cursor, page, info.PageCount, info.PageSize, info.TotalRowCount, transferred);
        }

        /// <summary>
        /// Có paging: cắt theo key, trả trang 0, nhiều trang thì lưu cursor. Không paging mà vượt maxRowsWithoutPaging → RESULT_TOO_LARGE.
        /// message rỗng thì lấy message của mã trong Message.xml
        /// </summary>
        private async Task<FormOutcome> CompleteAsync(ImportForm form, FormCallContext call, FormResultSet result, CancellationToken cancellationToken)
        {
            string message = result.Message.Length == 0 && options.Messages.Get(result.Code) is Message known ? known.message : result.Message;

            if (result.IsSuccess && form.Paging is ImportPaging paging)
            {
                PagedResult paged = DataSetPager.Paginate(result.Tables, paging.Key, paging.PageSize);
                string? cursor = paged.Pages.Count > 1
                    ? await cursors.SaveAsync(call.UId, form.Name, call.Unit, paged, cancellationToken)
                    : null;

                int? transferred = null;
                if (form.Type == ImportFormType.Transfer)
                {
                    if (await MarkTransferredAsync(form, paged.Pages[0].Keys, call, cancellationToken) is FormFailed failed)
                    {
                        return failed;
                    }
                    transferred = paged.Pages[0].Keys.Count;
                }
                return new FormPaged(result.Code, message, CursorStore.ToJson(paged.Pages[0]), cursor, 0, paged.Pages.Count, paged.PageSize, paged.TotalRowCount, transferred);
            }

            int rowCount = result.RowCount;
            if (result.IsSuccess && rowCount > appConfig.maxRowsWithoutPaging)
            {
                return new FormFailed("RESULT_TOO_LARGE", $"{rowCount} dòng vượt {appConfig.maxRowsWithoutPaging}: khai <paging> cho form {form.Name}");
            }

            return new FormCompleted(result.Code, message, result.Tables);
        }

        /// <summary>
        /// Transfer (4.4 #5): đánh dấu đã chuyển đúng các key của trang sắp trả, trước khi trả dữ liệu.
        /// Đánh dấu lỗi thì không trả dữ liệu trang đó (dữ liệu chưa đánh dấu sẽ ra lại ở lần lấy sau, không mất).
        /// Gọi lại cùng trang thì đánh dấu lại: query transferred phải chạy lại không lỗi
        /// </summary>
        private async Task<FormFailed?> MarkTransferredAsync(ImportForm form, IReadOnlyList<string> keys, FormCallContext call, CancellationToken cancellationToken)
        {
            string code = await executor.MarkTransferredAsync(form, keys, call, cancellationToken);
            return code == FormResultSet.Success ? null : new FormFailed("TRANSFER_MARK_FAILED", $"Query transferred trả {code}");
        }
    }
}
