using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Business;
using Models;
using Models.Options;
using Newtonsoft.Json;
using Security;

namespace Web.Custom
{
    /// <summary>
    /// Route nghiệp vụ v3: POST /api/{resource} và /api/{resource}/{verb} (KE_HOACH 4.1).
    /// Chữ ký đã kiểm ở SignatureMiddleware. Thứ tự: route → quyền form → unitCode → dạng body → giá trị field
    /// → Idempotency-Key → checking → processing
    /// </summary>
    public static partial class FormEndpoint
    {
        public const string ReplayedHeader = "Idempotent-Replayed";

        [GeneratedRegex("^[\\x21-\\x7E]{1,128}$")]
        private static partial Regex IdempotencyKeyFormat();

        private sealed record FormResponse(int Status, string Json, bool Replayed = false);

        public static void MapForms(this IEndpointRouteBuilder endpoints)
        {
            // Mọi method: route literal (auth, _meta) được ưu tiên, còn lại vào đây để trả 404/405 thống nhất
            endpoints.Map("/api/{**path}", HandleAsync);
        }

        private static async Task HandleAsync(HttpContext context)
        {
            FormResponse response = await BuildAsync(context);

            context.Response.StatusCode = response.Status;
            context.Response.ContentType = "application/json; charset=utf-8";
            if (response.Status == StatusCodes.Status405MethodNotAllowed)
            {
                context.Response.Headers.Allow = "POST";
            }
            if (response.Replayed)
            {
                context.Response.Headers[ReplayedHeader] = "true";
            }
            await context.Response.WriteAsync(response.Json, Encoding.UTF8);
        }

        private static async Task<FormResponse> BuildAsync(HttpContext context)
        {
            var services = context.RequestServices;
            var runtime = services.GetRequiredService<FormRuntime>();
            var appConfig = services.GetRequiredService<AppConfig>();
            var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;

            switch (runtime.Catalog.Resolve(context.Request.Method, context.Request.Path.Value ?? "", out ImportForm? form))
            {
                case RouteMatch.NotFound:
                    return Error(context, "NOT_FOUND");
                case RouteMatch.MethodNotAllowed:
                    return Error(context, "METHOD_NOT_ALLOWED");
            }

            context.Items[RequestLogMiddleware.FormItem] = form!.Name;

            // Bước 6: quyền form (mặc định từ chối), rồi đơn vị
            if (!signer.CanAccessForm(form.Name))
            {
                return Error(context, "FORBIDDEN");
            }

            string unit = context.Request.Query["unitCode"].ToString().Trim();
            if (form.UnitRequired && unit.Length == 0)
            {
                return Error(context, "UNIT_REQUIRED");
            }
            if (unit.Length > 0)
            {
                string unitCode = await services.GetRequiredService<IUnitRightsChecker>().CheckAsync(signer.UserId!.Value, unit, form.UnitAction, context.RequestAborted);
                if (unitCode != "SUCCESS")
                {
                    return Error(context, unitCode);
                }
            }

            // Trang tiếp theo: lấy từ cursor, không chạy lại query (quyền form và đơn vị vẫn kiểm ở trên)
            string cursor = context.Request.Query["cursor"].ToString().Trim();
            if (cursor.Length > 0)
            {
                return await PageAsync(context, form, signer, unit, cursor);
            }

            byte[] rawBody = (byte[])context.Items[SignatureMiddleware.RawBodyItem]!;
            MappedBody body = JsonBodyMapper.Map(form, rawBody, appConfig.maxDocumentsPerRequest, runtime.Irregular);
            if (body.ShapeError != null)
            {
                return Error(context, "INVALID_SHAPE", body.ShapeError);
            }
            if (body.Errors.Count > 0)
            {
                return ValidationErrors(context, body.Errors.Select(e => (e.Position, e.Field, e.Code)));
            }

            bool dryRun = context.Request.Query["dryRun"].ToString().Trim().ToLowerInvariant() is "true" or "1";
            var call = new FormCallContext(signer.UId, signer.Name, signer.UserId!.Value, signer.NameErp, signer.ErpAdmin, unit, dryRun);

            // dryRun không ghi gì nên không cần Idempotency-Key
            string key = context.Request.Headers[SignatureMiddleware.IdempotencyKeyHeader].ToString().Trim();
            if (key.Length == 0 || dryRun)
            {
                return await ExecuteAsync(context, form, body, call);
            }
            if (!IdempotencyKeyFormat().IsMatch(key))
            {
                return Error(context, "INVALID_IDEMPOTENCY_KEY");
            }

            // Cùng key phải cùng form, cùng query và cùng body (byte thô)
            string fingerprint = MessageSignature.Sha256Hex(Encoding.UTF8.GetBytes(
                form.Name + "\n" + MessageSignature.CanonicalQuery(context.Request.QueryString.Value) + "\n" + MessageSignature.Sha256Hex(rawBody)));

            var store = services.GetRequiredService<IIdempotencyStore>();
            IdempotencyResult reservation = await store.ReserveAsync(signer.UId, key, form.Name, fingerprint, context.RequestAborted);
            switch (reservation.State)
            {
                case IdempotencyState.Replay:
                    return new FormResponse(reservation.StatusCode, reservation.ResponseBody, Replayed: true);
                case IdempotencyState.Conflict:
                    return Error(context, "IDEMPOTENCY_CONFLICT");
                case IdempotencyState.InProgress:
                    return Error(context, "IDEMPOTENCY_IN_PROGRESS");
            }

            try
            {
                FormResponse response = await ExecuteAsync(context, form, body, call);
                await store.CompleteAsync(signer.UId, key, response.Status, response.Json, CancellationToken.None);
                return response;
            }
            catch
            {
                await store.ReleaseAsync(signer.UId, key);
                throw;
            }
        }

        private static async Task<FormResponse> ExecuteAsync(HttpContext context, ImportForm form, MappedBody body, FormCallContext call)
        {
            FormExecutionResult result = await context.RequestServices.GetRequiredService<IFormExecutor>().ExecuteAsync(form, body, call, context.RequestAborted);

            if (result.CheckingErrors.Count > 0)
            {
                return ValidationErrors(context, result.CheckingErrors.Select(e => (e.Position, e.Field, e.Code)));
            }
            if (call.DryRun)
            {
                return Json(200, new
                {
                    code = "SUCCESS",
                    success = true,
                    message = "",
                    request_id = RequestId(context),
                    dryRun = true,
                    documents = body.DocumentCount
                });
            }
            return await DataSetResponseAsync(context, form, call, result.Data!);
        }

        /// <summary>
        /// ?cursor=…&amp;page=n: trang n (từ 0) đã lưu. Cursor phải của chính user, cùng form và cùng unitCode
        /// </summary>
        private static async Task<FormResponse> PageAsync(HttpContext context, ImportForm form, SignerKeys signer, string unit, string cursor)
        {
            var store = context.RequestServices.GetRequiredService<ICursorStore>();
            CursorInfo? info = await store.GetInfoAsync(signer.UId, cursor, context.RequestAborted);
            if (info == null || info.Form != form.Name || !string.Equals(info.Unit, unit, StringComparison.OrdinalIgnoreCase))
            {
                return Error(context, "CURSOR_EXPIRED");
            }

            string pageText = context.Request.Query["page"].ToString().Trim();
            if (!int.TryParse(pageText.Length == 0 ? "0" : pageText, out int page) || page < 0 || page >= info.PageCount)
            {
                return Error(context, "PAGE_OUT_OF_RANGE", $"page phải từ 0 tới {info.PageCount - 1}");
            }

            string? json = await store.GetPageJsonAsync(signer.UId, cursor, page, context.RequestAborted);
            if (json == null)
            {
                return Error(context, "CURSOR_EXPIRED");
            }

            int? transferred = null;
            if (form.Type == ImportFormType.Transfer)
            {
                IReadOnlyList<string>? keys = await store.GetPageKeysAsync(signer.UId, cursor, page, context.RequestAborted);
                if (keys == null)
                {
                    return Error(context, "CURSOR_EXPIRED");
                }
                var call = new FormCallContext(signer.UId, signer.Name, signer.UserId!.Value, signer.NameErp, signer.ErpAdmin, info.Unit, false);
                if (await MarkTransferredAsync(context, form, keys, call) is FormResponse failed)
                {
                    return failed;
                }
                transferred = keys.Count;
            }

            return PagedResponse(context, "SUCCESS", "", json, cursor, page, info.PageCount, info.PageSize, info.TotalRowCount, transferred);
        }

        /// <summary>
        /// Transfer (4.4 #5): đánh dấu đã chuyển đúng các key của trang sắp trả, trước khi trả dữ liệu.
        /// Đánh dấu lỗi thì không trả dữ liệu trang đó (dữ liệu chưa đánh dấu sẽ ra lại ở lần lấy sau, không mất).
        /// Gọi lại cùng trang thì đánh dấu lại: query transferred phải chạy lại không lỗi
        /// </summary>
        private static async Task<FormResponse?> MarkTransferredAsync(HttpContext context, ImportForm form, IReadOnlyList<string> keys, FormCallContext call)
        {
            string code = await context.RequestServices.GetRequiredService<IFormExecutor>().MarkTransferredAsync(form, keys, call, context.RequestAborted);
            return code == "SUCCESS" ? null : Error(context, "TRANSFER_MARK_FAILED", $"Query transferred trả {code}");
        }

        /// <summary>
        /// Dataset processing (4.4): bảng 0 code, message, tables; bảng 1..N dữ liệu, tên theo tables hoặc data / data1..dataN
        /// </summary>
        private static async Task<FormResponse> DataSetResponseAsync(HttpContext context, ImportForm form, FormCallContext call, DataSet data)
        {
            var options = context.RequestServices.GetRequiredService<Options>();
            var appConfig = context.RequestServices.GetRequiredService<AppConfig>();

            string code = FormExecutor.ResultCode(data);
            DataTable? head = data.Tables.Count > 0 ? data.Tables[0] : null;
            string message = head != null && head.Columns.Contains("message") && head.Rows.Count > 0 ? head.Rows[0]["message"]?.ToString() ?? "" : "";
            string[] names = head != null && head.Columns.Contains("tables") && head.Rows.Count > 0
                ? (head.Rows[0]["tables"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            Dictionary<string, DataTable> tables = [];
            int dataCount = Math.Max(0, data.Tables.Count - 1);
            for (int i = 1; i < data.Tables.Count; i++)
            {
                string name = i - 1 < names.Length ? names[i - 1] : dataCount == 1 ? "data" : $"data{i}";
                tables[name] = data.Tables[i];
            }

            Message? known = options.Messages.Get(code);
            int status = code == "SUCCESS" ? 200 : known?.httpStatusCode ?? 400;
            if (message.Length == 0 && known != null)
            {
                message = known.message;
            }

            if (code == "SUCCESS" && form.Paging is ImportPaging paging)
            {
                // Cắt theo key, trả trang 0; nhiều trang thì lưu cursor cho các trang sau
                PagedResult paged = DataSetPager.Paginate(tables, paging.Key, paging.PageSize);
                string? cursor = paged.Pages.Count > 1
                    ? await context.RequestServices.GetRequiredService<ICursorStore>().SaveAsync(call.UId, form.Name, call.Unit, paged, context.RequestAborted)
                    : null;

                int? transferred = null;
                if (form.Type == ImportFormType.Transfer)
                {
                    if (await MarkTransferredAsync(context, form, paged.Pages[0].Keys, call) is FormResponse failed)
                    {
                        return failed;
                    }
                    transferred = paged.Pages[0].Keys.Count;
                }
                return PagedResponse(context, code, message, CursorStore.ToJson(paged.Pages[0]), cursor, 0, paged.Pages.Count, paged.PageSize, paged.TotalRowCount, transferred);
            }

            int rowCount = tables.Values.Sum(t => t.Rows.Count);
            if (code == "SUCCESS" && rowCount > appConfig.maxRowsWithoutPaging)
            {
                return Error(context, "RESULT_TOO_LARGE", $"{rowCount} dòng vượt {appConfig.maxRowsWithoutPaging}: khai <paging> cho form {form.Name}");
            }

            return Json(status, new
            {
                code,
                success = code == "SUCCESS",
                message,
                request_id = RequestId(context),
                data = tables
            });
        }

        /// <summary>
        /// Response có phân trang (4.4): data là JSON đã dựng sẵn của trang, paging cho biết cursor và số trang
        /// </summary>
        private static FormResponse PagedResponse(HttpContext context, string code, string message, string dataJson, string? cursor,
            int pageIndex, int pageCount, int pageSize, int totalRowCount, int? transferredCount = null)
        {
            var response = new Newtonsoft.Json.Linq.JObject
            {
                ["code"] = code,
                ["success"] = code == "SUCCESS",
                ["message"] = message,
                ["request_id"] = RequestId(context),
                ["data"] = Newtonsoft.Json.Linq.JToken.Parse(dataJson),
                ["paging"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["cursor"] = cursor,
                    ["page_index"] = pageIndex,
                    ["page_count"] = pageCount,
                    ["page_size"] = pageSize,
                    ["total_row_count"] = totalRowCount
                }
            };
            if (transferredCount != null)
            {
                // Form Transfer: số chứng từ của trang này đã được đánh dấu đã chuyển
                ((Newtonsoft.Json.Linq.JObject)response["paging"]!)["transferred_count"] = transferredCount;
            }
            return new FormResponse(200, response.ToString(Formatting.None));
        }

        private static FormResponse ValidationErrors(HttpContext context, IEnumerable<(string Position, string Field, string Code)> errors)
        {
            var options = context.RequestServices.GetRequiredService<Options>();
            Message? head = options.Messages.Get("VALIDATION_FAILED");

            return Json(head?.httpStatusCode ?? 400, new
            {
                code = "VALIDATION_FAILED",
                success = false,
                message = head?.message ?? "",
                request_id = RequestId(context),
                errors = errors.Select(e => new { position = e.Position, field = e.Field, code = e.Code, message = options.Messages.Get(e.Code)?.message ?? e.Code })
            });
        }

        /// <summary>Lỗi trước khi chạy form, cùng dạng với ResponseHelper.WriteJsonAsync</summary>
        private static FormResponse Error(HttpContext context, string code, string? message = null)
        {
            var response = context.RequestServices.GetRequiredService<ResponseManager>().GetResponse(code, message ?? "");
            return new FormResponse(response.statusCode, JsonConvert.SerializeObject(response));
        }

        private static string? RequestId(HttpContext context) => context.Items["requestID"]?.ToString();

        private static FormResponse Json(int status, object value) => new(status, JsonConvert.SerializeObject(value));
    }
}
