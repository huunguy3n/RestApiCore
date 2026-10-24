using System.Text;
using Business;
using Models;
using Models.Options;
using Newtonsoft.Json;

namespace Web.Custom
{
    /// <summary>
    /// Portal đối tác (KE_HOACH 5.3): đăng nhập POST /api/auth/portal bằng restApiUser.name + password mã hóa RSA-OAEP tại trình duyệt,
    /// và các route _meta đọc định nghĩa tài nguyên. JWT scope=portal chỉ gọi được GET /api/_meta/* (SignatureMiddleware chặn route khác)
    /// </summary>
    public static class PortalEndpoints
    {
        public const string PortalPath = "/api/auth/portal";

        public static void MapPortal(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost(PortalPath, LoginAsync);
            endpoints.MapGet("/api/_meta/resources", ListResources);
            endpoints.MapGet("/api/_meta/resources/{form}", DescribeResource);
            endpoints.MapGet("/api/_meta/errors", ListErrors);
        }

        private sealed record PortalLogin(string? userName, string? kid, string? credential);

        /// <summary>Kiểm dạng body ở đây; các bước xác thực ở PortalLoginService</summary>
        private static async Task LoginAsync(HttpContext context, ResponseManager responseManager, PortalLoginService portalLogin)
        {
            PortalLogin? login = await ReadJsonAsync<PortalLogin>(context.Request);
            if (login is not { userName.Length: > 0 and <= 32, kid.Length: > 0, credential.Length: > 0 })
            {
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse("INVALID_SHAPE", "body phải có userName, kid, credential"));
                return;
            }

            PortalLoginResult result = await portalLogin.LoginAsync(login.userName, login.kid, login.credential,
                context.Connection.RemoteIpAddress?.ToString() ?? "UNKNOW", context.RequestAborted);
            if (result.AccountFound)
            {
                context.Items[RequestLogMiddleware.SignatureResultItem] = "PORTAL_LOGIN";
            }
            if (result.Token == null)
            {
                await ResponseHelper.WriteJsonAsync(context, responseManager.GetResponse(result.Code));
                return;
            }

            await WriteAsync(context, 200, new
            {
                code = "SUCCESS",
                success = true,
                message = "",
                requestId = context.Items["requestID"]?.ToString(),
                accessToken = result.Token.AccessToken,
                tokenType = "Bearer",
                expiresIn = result.Token.ExpiresIn,
                scope = "portal"
            });
        }

        /// <summary>Tài nguyên user được cấp quyền (restApiAllowForm)</summary>
        private static IResult ListResources(HttpContext context, FormRuntime runtime)
        {
            var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;
            var resources = runtime.Catalog.Forms
                .Where(f => f.Enable && signer.CanAccessForm(f.Name))
                .OrderBy(f => f.Resource, StringComparer.Ordinal).ThenBy(f => f.Verb, StringComparer.Ordinal)
                .Select(Summary);

            return Json(context, new { code = "SUCCESS", success = true, message = "", data = resources });
        }

        /// <summary>Định nghĩa một tài nguyên: khối, field, mẫu JSON (5.3)</summary>
        private static IResult DescribeResource(HttpContext context, string form, FormRuntime runtime, ResponseManager responseManager)
        {
            var signer = (SignerKeys)context.Items[SignatureMiddleware.SignerItem]!;
            ImportForm? found = runtime.Catalog.Forms.FirstOrDefault(f => f.Enable && string.Equals(f.Name, form, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                return Error(responseManager, "NOT_FOUND");
            }
            if (!signer.CanAccessForm(found.Name))
            {
                return Error(responseManager, "FORBIDDEN");
            }

            return Json(context, new
            {
                code = "SUCCESS",
                success = true,
                message = "",
                data = new
                {
                    summary = Summary(found),
                    blocks = found.Blocks.Select(b => new
                    {
                        name = b.Name,
                        isRoot = b.IsRoot,
                        isArray = !b.IsRoot,
                        key = b.Key.Length > 0 ? b.Key : null,
                        fields = VisibleFields(b).Select(f => new
                        {
                            name = f.Name,
                            type = f.Type.ToString().ToLowerInvariant(),
                            maxLength = f.EffectiveMaxLength ?? f.MaxLength,
                            required = !f.AllowNull,
                            format = f.Type == ImportFieldType.DateTime ? (f.Format.Length > 0 ? f.Format + " hoặc ISO 8601" : "ISO 8601") : null,
                            defaultValue = f.DefaultValue,
                            upperCase = f.UpperCase,
                            description = f.Description,
                            example = f.Example,
                            errorCodes = f.Checks.Select(c => c.ErrorCode).Distinct()
                        })
                    }),
                    sample = Sample(found)
                }
            });
        }

        /// <summary>Bảng mã lỗi từ Message.xml</summary>
        private static IResult ListErrors(HttpContext context, Options options)
        {
            var errors = options.Messages.Select(m => new { code = m.code, httpStatus = m.httpStatusCode, message = m.message }).OrderBy(m => m.code, StringComparer.Ordinal);
            return Json(context, new { code = "SUCCESS", success = true, message = "", data = errors });
        }

        private static object Summary(ImportForm form) => new
        {
            form = form.Name,
            method = "POST",
            route = form.Route,
            verb = form.Verb,
            resource = form.Resource,
            type = form.Type.ToString(),
            description = form.Description,
            unitRequired = form.UnitRequired,
            unitAction = form.UnitAction,
            jsonNaming = form.JsonNaming,
            columnNaming = form.ColumnNaming,
            paging = form.Paging == null ? null : new { pageSize = form.Paging.PageSize, key = form.Paging.Key }
        };

        private static IEnumerable<ImportField> VisibleFields(ImportBlock block) => block.Fields.Where(f => !f.Hidden && !f.IsInheritedKey);

        /// <summary>
        /// Mẫu body: form nhiều khối dạng { "gốc": { … }, "con": [ { … } ] }, form một khối dạng phẳng (4.3)
        /// </summary>
        public static Dictionary<string, object?> Sample(ImportForm form)
        {
            Dictionary<string, object?> Row(ImportBlock block) => VisibleFields(block).ToDictionary(f => f.Name, SampleValue);

            if (form.Blocks.Count == 1)
            {
                return Row(form.Root);
            }
            Dictionary<string, object?> sample = new() { [form.Root.Name] = Row(form.Root) };
            foreach (ImportBlock block in form.Blocks.Skip(1))
            {
                sample[block.Name] = new[] { Row(block) };
            }
            return sample;
        }

        private static object? SampleValue(ImportField field)
        {
            if (field.Example.Length > 0)
            {
                return field.Type switch
                {
                    ImportFieldType.Numeric when decimal.TryParse(field.Example, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal number) => number,
                    ImportFieldType.Int when int.TryParse(field.Example, out int integer) => integer,
                    ImportFieldType.UnixTime when long.TryParse(field.Example, out long seconds) => seconds,
                    ImportFieldType.Bool when bool.TryParse(field.Example, out bool flag) => flag,
                    _ => field.Example
                };
            }
            return field.Type switch
            {
                ImportFieldType.Numeric => 0m,
                ImportFieldType.Int => 0,
                ImportFieldType.Bool => false,
                ImportFieldType.UnixTime => 1792574400L,
                ImportFieldType.DateTime => field.Format.Length > 0 ? new DateTime(2026, 10, 21).ToString(field.Format, System.Globalization.CultureInfo.InvariantCulture) : "2026-10-21",
                _ => ""
            };
        }

        private static async Task<T?> ReadJsonAsync<T>(HttpRequest request) where T : class
        {
            using MemoryStream memory = new();
            await request.Body.CopyToAsync(memory);
            return Parse<T>(memory.ToArray());
        }

        private static T? Parse<T>(byte[]? data) where T : class
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(data);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static IResult Error(ResponseManager responseManager, string code)
        {
            var response = responseManager.GetResponse(code);
            return Results.Text(JsonConvert.SerializeObject(response), "application/json; charset=utf-8", Encoding.UTF8, response.statusCode);
        }

        private static IResult Json(HttpContext context, object value) =>
            Results.Text(JsonConvert.SerializeObject(value), "application/json; charset=utf-8", Encoding.UTF8, 200);

        private static async Task WriteAsync(HttpContext context, int status, object value)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonConvert.SerializeObject(value), Encoding.UTF8);
        }
    }
}
