using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Business;
using Business.V3;
using Models;
using Models.Options;
using Models.V3;
using Newtonsoft.Json;

namespace Web.Custom
{
    /// <summary>
    /// Bảng route form v3 và mẫu ký tự đặc biệt, dựng một lần lúc khởi động
    /// </summary>
    public sealed class V3FormRuntime(ImportFormCatalog catalog, Regex? irregular)
    {
        public ImportFormCatalog Catalog { get; } = catalog;
        public Regex? Irregular { get; } = irregular;
    }

    /// <summary>
    /// Route nghiệp vụ v3: POST /api/v3/{resource} và /api/v3/{resource}/{verb} (KE_HOACH_V3 4.1).
    /// Chữ ký đã kiểm ở SignatureMiddleware. Thứ tự: route → quyền form → unitCode → dạng body → giá trị field → checking → processing
    /// </summary>
    public static class V3FormEndpoint
    {
        public static void MapV3Forms(this IEndpointRouteBuilder endpoints)
        {
            // Mọi method: route literal (auth, _meta) được ưu tiên, còn lại vào đây để trả 404/405 thống nhất
            endpoints.Map("/api/v3/{**path}", HandleAsync);
        }

        private static async Task HandleAsync(HttpContext context)
        {
            var services = context.RequestServices;
            var runtime = services.GetRequiredService<V3FormRuntime>();
            var appConfig = services.GetRequiredService<AppConfig>();
            var responseManager = services.GetRequiredService<ResponseManager>();
            var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;

            switch (runtime.Catalog.Resolve(context.Request.Method, context.Request.Path.Value ?? "", out ImportForm? form))
            {
                case RouteMatch.NotFound:
                    await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("NOT_FOUND"));
                    return;
                case RouteMatch.MethodNotAllowed:
                    context.Response.Headers.Allow = "POST";
                    await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("METHOD_NOT_ALLOWED"));
                    return;
            }

            // Bước 6: quyền form (mặc định từ chối), rồi đơn vị
            if (!signer.CanAccessForm(form!.Name))
            {
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("FORBIDDEN"));
                return;
            }

            string unit = context.Request.Query["unitCode"].ToString().Trim();
            if (form.UnitRequired && unit.Length == 0)
            {
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("UNIT_REQUIRED"));
                return;
            }
            if (unit.Length > 0)
            {
                string unitCode = await services.GetRequiredService<IUnitRightsChecker>().CheckAsync(signer.UserId!.Value, unit, form.UnitAction, context.RequestAborted);
                if (unitCode != "SUCCESS")
                {
                    await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse(unitCode));
                    return;
                }
            }

            byte[] rawBody = (byte[])context.Items[SignatureMiddleware.RawBodyItem]!;
            MappedBody body = JsonBodyMapper.Map(form, rawBody, appConfig.maxDocumentsPerRequest, runtime.Irregular);
            if (body.ShapeError != null)
            {
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("INVALID_SHAPE", body.ShapeError));
                return;
            }
            if (body.Errors.Count > 0)
            {
                await WriteValidationErrorsAsync(context, body.Errors.Select(e => (e.Position, e.Field, e.Code)));
                return;
            }

            bool dryRun = context.Request.Query["dryRun"].ToString().Trim().ToLowerInvariant() is "true" or "1";
            var call = new FormCallContext(signer.UId, signer.Name, signer.UserId!.Value, signer.NameErp, signer.ErpAdmin, unit, dryRun);

            FormExecutionResult result = await services.GetRequiredService<IFormExecutor>().ExecuteAsync(form, body, call, context.RequestAborted);

            if (result.CheckingErrors.Count > 0)
            {
                await WriteValidationErrorsAsync(context, result.CheckingErrors.Select(e => (e.Position, e.Field, e.Code)));
                return;
            }
            if (dryRun)
            {
                await WriteAsync(context, 200, new
                {
                    code = "SUCCESS",
                    success = true,
                    message = "",
                    request_id = RequestId(context),
                    dryRun = true,
                    documents = body.DocumentCount
                });
                return;
            }

            await WriteDataSetAsync(context, result.Data!);
        }

        /// <summary>
        /// Dataset processing (4.4): bảng 0 code, message, tables; bảng 1..N dữ liệu, tên theo tables hoặc data / data1..dataN
        /// </summary>
        private static async Task WriteDataSetAsync(HttpContext context, DataSet data)
        {
            var options = context.RequestServices.GetRequiredService<Options>();

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

            await WriteAsync(context, status, new
            {
                code,
                success = code == "SUCCESS",
                message,
                request_id = RequestId(context),
                data = tables
            });
        }

        private static async Task WriteValidationErrorsAsync(HttpContext context, IEnumerable<(string Position, string Field, string Code)> errors)
        {
            var options = context.RequestServices.GetRequiredService<Options>();
            Message? head = options.Messages.Get("VALIDATION_FAILED");

            await WriteAsync(context, head?.httpStatusCode ?? 400, new
            {
                code = "VALIDATION_FAILED",
                success = false,
                message = head?.message ?? "",
                request_id = RequestId(context),
                errors = errors.Select(e => new { position = e.Position, field = e.Field, code = e.Code, message = options.Messages.Get(e.Code)?.message ?? e.Code })
            });
        }

        private static string? RequestId(HttpContext context) => context.Items["requestID"]?.ToString();

        private static async Task WriteAsync(HttpContext context, int status, object value)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(value), Encoding.UTF8);
        }
    }
}
