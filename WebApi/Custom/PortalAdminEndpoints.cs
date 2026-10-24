using Business;
using Models;
using Newtonsoft.Json;

namespace Web.Custom
{
    /// <summary>
    /// API quản trị portal (/api/_portal/*). Chỉ JWT scope=portal; quyền kiểm lại ở server theo vai trò trong DB cho MỌI request
    /// (quyền không nằm trong JWT nên thu hồi vai trò có hiệu lực ngay). userinfo2.admin không có vai trò gì ở đây.
    /// JWT đi bằng header Authorization (không cookie) nên không có bề mặt CSRF; CSP của WebPortal chặn script lạ
    /// </summary>
    public static class PortalAdminEndpoints
    {
        public const string BasePath = "/api/_portal";
        private const int MaxBodyBytes = 64 * 1024;

        public static void MapPortalAdmin(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet(BasePath + "/me", MeAsync);
            endpoints.MapGet(BasePath + "/users", ListUsersAsync);
            endpoints.MapGet(BasePath + "/users/{name}/forms", GetFormsAsync);
            endpoints.MapPut(BasePath + "/users/{name}/forms", SetFormsAsync);
            endpoints.MapGet(BasePath + "/logs", ListLogsAsync);
            endpoints.MapGet(BasePath + "/logs/{reqId}", GetLogAsync);
        }

        private sealed record SetForms(string[]? forms);

        private static (int UId, string Name)? Actor(HttpContext context)
        {
            // Token API (scope=api) không dùng được ở đây; signer do SignatureMiddleware đặt sau khi kiểm user còn hiệu lực
            if (context.User.FindFirst("scope")?.Value != "portal" || context.Items[SignatureMiddleware.SignerItem] is not SignerKeys signer)
            {
                return null;
            }
            return (signer.UId, signer.Name);
        }

        private static async Task MeAsync(HttpContext context, PortalAuthorizationService authorization)
        {
            if (Actor(context) is not var (uId, name))
            {
                await Write(context, new ServiceResult("FORBIDDEN"));
                return;
            }
            PortalAccess access = await authorization.GetAccessAsync(uId, context.RequestAborted);
            await Write(context, new ServiceResult("SUCCESS", new
            {
                userName = name,
                roles = access.Roles.Order().Prepend("partner"),
                permissions = access.Permissions.Order(StringComparer.Ordinal)
            }));
        }

        private static async Task ListUsersAsync(HttpContext context, UserAdminService users, string? search, int? page, int? pageSize)
        {
            if (Actor(context) is not var (uId, name))
            {
                await Write(context, new ServiceResult("FORBIDDEN"));
                return;
            }
            await Write(context, await users.ListAsync(uId, name, search, page ?? 1, pageSize ?? 25, RequestId(context), context.RequestAborted));
        }

        private static async Task GetFormsAsync(HttpContext context, FormPermissionService service, string name)
        {
            if (Actor(context) is not var (uId, actorName))
            {
                await Write(context, new ServiceResult("FORBIDDEN"));
                return;
            }
            await Write(context, await service.GetAsync(uId, actorName, name, RequestId(context), context.RequestAborted));
        }

        private static async Task SetFormsAsync(HttpContext context, FormPermissionService service, string name)
        {
            if (Actor(context) is not var (uId, actorName))
            {
                await Write(context, new ServiceResult("FORBIDDEN"));
                return;
            }

            SetForms? body = await ReadBodyAsync(context);
            await Write(context, body == null
                ? new ServiceResult("INVALID_SHAPE")
                : await service.SetAsync(uId, actorName, name, body.forms, RequestId(context), context.RequestAborted));
        }

        private static async Task ListLogsAsync(HttpContext context, RequestLogViewService logs, DateTime? from, DateTime? to, string? user, string? form, int? status, string? reqId, int? page, int? pageSize)
        {
            if (Actor(context) is not var (uId, name))
            {
                await Write(context, new ServiceResult("FORBIDDEN"));
                return;
            }
            await Write(context, await logs.ListAsync(uId, name, from, to, user, form, status, reqId, page ?? 1, pageSize ?? 25, RequestId(context), context.RequestAborted));
        }

        private static async Task GetLogAsync(HttpContext context, RequestLogViewService logs, string reqId)
        {
            if (Actor(context) is not var (uId, name))
            {
                await Write(context, new ServiceResult("FORBIDDEN"));
                return;
            }
            await Write(context, await logs.GetAsync(uId, name, reqId, RequestId(context), context.RequestAborted));
        }

        private static string? RequestId(HttpContext context) => context.Items["requestID"]?.ToString();

        private static async Task<SetForms?> ReadBodyAsync(HttpContext context)
        {
            if (context.Request.ContentLength > MaxBodyBytes)
            {
                return null;
            }
            using MemoryStream memory = new();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (memory.Length + read > MaxBodyBytes)
                {
                    return null;
                }
                memory.Write(chunk, 0, read);
            }
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<SetForms>(memory.ToArray());
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>Mã lỗi nghiệp vụ → HTTP status từ Message.xml; thành công trả { code, success, data }</summary>
        private static async Task Write(HttpContext context, ServiceResult result)
        {
            if (result.Success)
            {
                await ResponseHelper.WriteRawAsync(context, 200, JsonConvert.SerializeObject(new { code = "SUCCESS", success = true, message = "", data = result.Data }));
                return;
            }
            await ResponseHelper.WriteJsonAsync(context, context.RequestServices.GetRequiredService<ResponseManager>().GetResponse(result.Code));
        }
    }
}
