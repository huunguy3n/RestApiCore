using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Web;
using System.Web.Script.Serialization;
using System.Web.SessionState;

namespace FboWeb.ApiLog
{
    /// <summary>
    /// AppHandler/ApiLogView.ashx — JSON cho trang Main/zcrptapilog.aspx, chỉ đọc log (trừ dòng audit khi mở body).
    /// action=list (danh sách + thống kê theo bộ lọc), detail (metadata một request), body (body đã che bí mật, ghi zcApiLogViewAudit trước).
    /// Quyền: đã đăng nhập FBO và (admin hoặc có quyền menu sysid zcrptApiLog). Lỗi không bao giờ trả chi tiết SQL/exception.
    /// </summary>
    public sealed class ApiLogHandler : IHttpHandler, IReadOnlySessionState
    {
        public const string MenuSysId = "zcrptApiLog";

        public bool IsReusable { get { return false; } }

        public void ProcessRequest(HttpContext context)
        {
            HttpResponse response = context.Response;
            response.Cache.SetCacheability(HttpCacheability.NoCache);
            response.Cache.SetNoStore();
            response.AddHeader("X-Content-Type-Options", "nosniff");
            try
            {
                if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)) { Fail(context, 405, "METHOD_NOT_ALLOWED"); return; }

                FboSession session = FboSession.Read();
                string denied = session.Authorize(MenuSysId);
                if (denied != null) { Fail(context, denied == "UNAUTHORIZED" ? 401 : 403, denied); return; }

                string connection = session.AppConnectionString();
                if (connection == null) { Fail(context, 500, "NO_DATABASE"); return; }
                ApiLogRepository repository = new ApiLogRepository(connection);

                string action = context.Request.QueryString["action"] ?? "list";
                if (action == "list") List(context, repository);
                else if (action == "detail") Detail(context, repository);
                else if (action == "body") Body(context, repository, session);
                else Fail(context, 400, "INVALID_ACTION");
            }
            catch (SqlException) { Fail(context, 500, "DATABASE_ERROR"); }
            catch (Exception) { Fail(context, 500, "ERROR"); }
        }

        private static void List(HttpContext context, ApiLogRepository repository)
        {
            ApiLogFilter filter;
            string invalid = ApiLogQuery.TryParseList(context.Request.QueryString, DateTime.Today, out filter);
            if (invalid != null) { Fail(context, 400, invalid); return; }
            Write(context, 200, repository.List(filter));
        }

        private static void Detail(HttpContext context, ApiLogRepository repository)
        {
            Guid reqId;
            if (!TryReqId(context, out reqId)) return;
            Dictionary<string, object> detail = repository.Detail(reqId);
            if (detail == null) { Fail(context, 404, "NOT_FOUND"); return; }
            Write(context, 200, detail);
        }

        private static void Body(HttpContext context, ApiLogRepository repository, FboSession session)
        {
            Guid reqId;
            if (!TryReqId(context, out reqId)) return;
            LoggedBodies bodies = repository.ReadBodies(reqId);
            if (bodies == null) { Fail(context, 404, "NOT_FOUND"); return; }

            // Mở body là truy cập dữ liệu nghiệp vụ của khách: không ghi được vết thì không trả
            try { repository.WriteAudit(session.UserId, session.UserName ?? "", reqId, context.Request.UserHostAddress); }
            catch (SqlException) { Fail(context, 500, "AUDIT_UNAVAILABLE"); return; }

            Write(context, 200, new Dictionary<string, object>
            {
                { "success", true },
                { "request", LoggedBodies.Present(bodies.RequestKept, bodies.Request) },
                { "response", LoggedBodies.Present(bodies.ResponseKept, bodies.Response) }
            });
        }

        private static bool TryReqId(HttpContext context, out Guid reqId)
        {
            Guid? parsed;
            if (!ApiLogQuery.TryParseReqId(context.Request.QueryString["reqId"], out parsed) || !parsed.HasValue)
            {
                reqId = Guid.Empty;
                Fail(context, 400, "INVALID_REQ_ID");
                return false;
            }
            reqId = parsed.Value;
            return true;
        }

        private static void Fail(HttpContext context, int status, string code)
        {
            Write(context, status, new Dictionary<string, object> { { "success", false }, { "code", code } });
        }

        private static void Write(HttpContext context, int status, object value)
        {
            context.Response.TrySkipIisCustomErrors = true;
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Write(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value));
        }
    }
}
