using System.Text;
using Models.Options;
using Newtonsoft.Json;

namespace Web.Custom
{
    /// <summary>
    /// GET /api/_meta/errors: bảng mã lỗi (Message.xml). Cần JWT + chữ ký như route nghiệp vụ.
    /// WebApi không có giao diện và không có endpoint quản trị: hướng dẫn, danh sách form, cấp tài khoản nằm ở WebPortal / PortalApp
    /// </summary>
    public static class MetaEndpoints
    {
        public static void MapMeta(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/_meta/errors", ListErrors);
        }

        private static IResult ListErrors(Options options)
        {
            var errors = options.Messages.Select(m => new { code = m.code, httpStatus = m.httpStatusCode, message = m.message }).OrderBy(m => m.code, StringComparer.Ordinal);
            return Results.Text(JsonConvert.SerializeObject(new { code = "SUCCESS", success = true, message = "", data = errors }), "application/json; charset=utf-8", Encoding.UTF8, 200);
        }
    }
}
