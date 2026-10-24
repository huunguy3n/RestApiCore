using System.Text;
using System.Text.RegularExpressions;
using Business;
using Microsoft.AspNetCore.Mvc;
using Models;
using Security;

namespace Web.Custom
{
    /// <summary>
    /// Route nghiệp vụ v3: POST /api/{resource} và /api/{resource}/{verb} (KE_HOACH 4.1).
    /// Chữ ký đã kiểm ở SignatureMiddleware. Thứ tự: route → quyền form → unitCode → dạng body → giá trị field
    /// → Idempotency-Key → checking → processing. Quy tắc nghiệp vụ ở FormRequestService, JSON ở FormResponseWriter;
    /// ở đây chỉ đọc HTTP, giữ chỗ/trả lại theo Idempotency-Key (lưu nguyên status + body HTTP) và ghi response
    /// </summary>
    public static partial class FormEndpoint
    {
        public const string ReplayedHeader = "Idempotent-Replayed";

        [GeneratedRegex("^[\\x21-\\x7E]{1,128}$")]
        private static partial Regex IdempotencyKeyFormat();

        public static void MapForms(this IEndpointRouteBuilder endpoints)
        {
            // Mọi method: route literal (auth, _meta) được ưu tiên, còn lại vào đây để trả 404/405 thống nhất
            endpoints.Map("/api/{**path}", HandleAsync);
        }

        private static async Task HandleAsync(HttpContext context, [FromServices] FormRuntime runtime, [FromServices] FormRequestService forms,
            [FromServices] FormResponseWriter writer, [FromServices] IIdempotencyStore idempotency)
        {
            FormResponse response = await BuildAsync(context, runtime, forms, writer, idempotency);

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

        private static async Task<FormResponse> BuildAsync(HttpContext context, FormRuntime runtime, FormRequestService forms, FormResponseWriter writer, IIdempotencyStore idempotency)
        {
            switch (runtime.Catalog.Resolve(context.Request.Method, context.Request.Path.Value ?? "", out ImportForm? form))
            {
                case RouteMatch.NotFound:
                    return writer.Error("NOT_FOUND");
                case RouteMatch.MethodNotAllowed:
                    return writer.Error("METHOD_NOT_ALLOWED");
            }

            context.Items[RequestLogMiddleware.FormItem] = form!.Name;

            // SignatureMiddleware gắn signer cho mọi route phải ký; thiếu là cấu hình pipeline sai, không chạy form
            if (context.Items[SignatureMiddleware.SignerItem] is not SignerKeys signer)
            {
                return writer.Error("UNAUTHORIZED");
            }

            // Bước 6: quyền form (mặc định từ chối), rồi đơn vị
            string unit = context.Request.Query["unitCode"].ToString().Trim();
            if (await forms.AuthorizeAsync(form, signer, unit, context.RequestAborted) is FormFailed denied)
            {
                return writer.Write(context, denied);
            }

            // Trang tiếp theo: lấy từ cursor, không chạy lại query (quyền form và đơn vị vẫn kiểm ở trên)
            string cursor = context.Request.Query["cursor"].ToString().Trim();
            if (cursor.Length > 0)
            {
                return writer.Write(context, await forms.NextPageAsync(form, signer, unit, cursor, context.Request.Query["page"].ToString().Trim(), context.RequestAborted));
            }

            byte[] rawBody = (byte[])context.Items[SignatureMiddleware.RawBodyItem]!;
            var (body, rejected) = forms.MapBody(form, rawBody, runtime.Irregular);
            if (rejected != null)
            {
                return writer.Write(context, rejected);
            }

            bool dryRun = context.Request.Query["dryRun"].ToString().Trim().ToLowerInvariant() is "true" or "1";
            FormCallContext call = FormRequestService.CallContext(signer, unit, dryRun);

            // dryRun không ghi gì nên không cần Idempotency-Key
            string key = context.Request.Headers[SignatureMiddleware.IdempotencyKeyHeader].ToString().Trim();
            if (key.Length == 0 || dryRun)
            {
                return writer.Write(context, await forms.ExecuteAsync(form, body, call, context.RequestAborted));
            }
            if (!IdempotencyKeyFormat().IsMatch(key))
            {
                return writer.Error("INVALID_IDEMPOTENCY_KEY");
            }

            // Cùng key phải cùng form, cùng query và cùng body (byte thô)
            string fingerprint = MessageSignature.Sha256Hex(Encoding.UTF8.GetBytes(
                form.Name + "\n" + QueryFingerprint(context.Request.QueryString.Value) + "\n" + MessageSignature.Sha256Hex(rawBody)));

            IdempotencyResult reservation = await idempotency.ReserveAsync(signer.UId, key, form.Name, fingerprint, context.RequestAborted);
            switch (reservation.State)
            {
                case IdempotencyState.Replay:
                    return new FormResponse(reservation.StatusCode, reservation.ResponseBody, Replayed: true);
                case IdempotencyState.Conflict:
                    return writer.Error("IDEMPOTENCY_CONFLICT");
                case IdempotencyState.InProgress:
                    return writer.Error("IDEMPOTENCY_IN_PROGRESS");
            }

            try
            {
                FormResponse response = writer.Write(context, await forms.ExecuteAsync(form, body, call, context.RequestAborted));
                await idempotency.CompleteAsync(signer.UId, key, response.Status, response.Json, CancellationToken.None);
                return response;
            }
            catch
            {
                await idempotency.ReleaseAsync(signer.UId, key);
                throw;
            }
        }

        /// <summary>Query không nằm trong chữ ký nên idempotency tự gom: bỏ '?', sắp các cặp thô theo ordinal</summary>
        private static string QueryFingerprint(string? query) =>
            string.Join("&", (query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
    }
}
