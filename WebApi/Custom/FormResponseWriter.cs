using Business;
using Models.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Web.Custom
{
    /// <summary>Response đã dựng của route form: status, JSON, có phải trả lại theo Idempotency-Key không</summary>
    public sealed record FormResponse(int Status, string Json, bool Replayed = false);

    /// <summary>
    /// Hợp đồng JSON của route nghiệp vụ v3 (KE_HOACH 4.4): FormOutcome → status + JSON. HTTP status lấy theo Message.xml.
    /// Đổi định dạng ở đây là đổi hợp đồng với đối tác (và làm lệch response đã lưu cho Idempotency-Key)
    /// </summary>
    public sealed class FormResponseWriter(Options options, ResponseManager responseManager)
    {
        public FormResponse Write(HttpContext context, FormOutcome outcome) => outcome switch
        {
            FormFailed failed => Error(failed.Code, failed.Message),
            FormInvalid invalid => ValidationErrors(context, invalid.Errors),
            FormDryRun dryRun => Json(200, new
            {
                code = FormResultSet.Success,
                success = true,
                message = "",
                requestId = RequestId(context),
                dryRun = true,
                documents = dryRun.Documents
            }),
            FormCompleted completed => Completed(context, completed),
            FormPaged paged => Paged(context, paged),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.GetType().Name, "FormOutcome chưa có cách trả")
        };

        /// <summary>Lỗi trước khi chạy form, cùng dạng với ResponseHelper.WriteJsonAsync</summary>
        public FormResponse Error(string code, string? message = null)
        {
            var response = responseManager.GetResponse(code, message ?? "");
            return new FormResponse(response.statusCode, JsonConvert.SerializeObject(response));
        }

        /// <summary>
        /// Dataset processing (4.4): code SUCCESS → 200, mã khác → httpStatusCode của mã (không có thì 400); data = các bảng theo tên
        /// </summary>
        private FormResponse Completed(HttpContext context, FormCompleted completed)
        {
            Message? known = options.Messages.Get(completed.Code);
            int status = completed.Code == FormResultSet.Success ? 200 : known?.httpStatusCode ?? 400;

            return Json(status, new
            {
                code = completed.Code,
                success = completed.Code == FormResultSet.Success,
                message = completed.Message,
                requestId = RequestId(context),
                data = completed.Tables
            });
        }

        /// <summary>
        /// Response có phân trang (4.4): data là JSON đã dựng sẵn của trang, paging cho biết cursor và số trang
        /// </summary>
        private static FormResponse Paged(HttpContext context, FormPaged paged)
        {
            var response = new JObject
            {
                ["code"] = paged.Code,
                ["success"] = paged.Code == FormResultSet.Success,
                ["message"] = paged.Message,
                ["requestId"] = RequestId(context),
                ["data"] = JToken.Parse(paged.DataJson),
                ["paging"] = new JObject
                {
                    ["cursor"] = paged.Cursor,
                    ["pageIndex"] = paged.PageIndex,
                    ["pageCount"] = paged.PageCount,
                    ["pageSize"] = paged.PageSize,
                    ["totalRowCount"] = paged.TotalRowCount
                }
            };
            if (paged.TransferredCount != null)
            {
                // Form Transfer: số chứng từ của trang này đã được đánh dấu đã chuyển
                ((JObject)response["paging"]!)["transferredCount"] = paged.TransferredCount;
            }
            return new FormResponse(200, response.ToString(Formatting.None));
        }

        private FormResponse ValidationErrors(HttpContext context, IEnumerable<FormFieldError> errors)
        {
            Message? head = options.Messages.Get("VALIDATION_FAILED");

            return Json(head?.httpStatusCode ?? 400, new
            {
                code = "VALIDATION_FAILED",
                success = false,
                message = head?.message ?? "",
                requestId = RequestId(context),
                errors = errors.Select(e => new { position = e.Position, field = e.Field, code = e.Code, message = options.Messages.Get(e.Code)?.message ?? e.Code })
            });
        }

        private static string? RequestId(HttpContext context) => context.Items["requestID"]?.ToString();

        private static FormResponse Json(int status, object value) => new(status, JsonConvert.SerializeObject(value));
    }
}
