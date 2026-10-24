using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace WebPortal.Portal
{
    /// <summary>
    /// Hai cookie tách biệt: "cpanel" (SA) và "project" (Admin / user Api của một dự án). Cookie chỉ mang id phiên + dấu vết mật khẩu;
    /// quyền KHÔNG nằm trong cookie mà tính lại ở mỗi request từ PortalDb
    /// </summary>
    public static class PortalAuth
    {
        public const string CpanelScheme = "cpanel";
        public const string ProjectScheme = "project";

        public const string KindClaim = "kind";
        public const string AccountClaim = "aid";
        public const string ProjectClaim = "pid";
        public const string ProjectCodeClaim = "ma_da";
        public const string SessionClaim = "sid";
        public const string StampClaim = "stamp";

        /// <summary>HttpContext.Items: id phiên vừa bị từ chối (để trang đăng nhập báo lý do)</summary>
        public const string RejectedSessionItem = "portal.rejectedSession";

        /// <summary>Dấu vết mật khẩu: đổi mật khẩu thì mọi phiên cũ mất hiệu lực (16 hex của SHA-256(băm mật khẩu), không chứa băm)</summary>
        public static string StampOf(string passwordHash) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];

        public static ClaimsPrincipal CreatePrincipal(VerifiedAccount account, Guid sessionId, string passwordHash, string scheme)
        {
            List<Claim> claims =
            [
                new(ClaimTypes.Name, account.Login),
                new(ClaimTypes.GivenName, account.DisplayName),
                new(KindClaim, account.Kind),
                new(AccountClaim, account.AccountId.ToString()),
                new(SessionClaim, sessionId.ToString()),
                new(StampClaim, StampOf(passwordHash))
            ];
            if (account.ProjectId != null)
            {
                claims.Add(new Claim(ProjectClaim, account.ProjectId.Value.ToString()));
                claims.Add(new Claim(ProjectCodeClaim, account.MaDa!));
            }
            return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
        }

        public static PortalIdentity? ToIdentity(ClaimsPrincipal? principal)
        {
            if (principal?.Identity?.IsAuthenticated != true
                || !int.TryParse(principal.FindFirst(AccountClaim)?.Value, out int accountId)
                || !Guid.TryParse(principal.FindFirst(SessionClaim)?.Value, out Guid sessionId))
            {
                return null;
            }
            int? projectId = int.TryParse(principal.FindFirst(ProjectClaim)?.Value, out int pid) ? pid : null;
            return new PortalIdentity(principal.FindFirst(KindClaim)?.Value ?? "", accountId, principal.Identity.Name ?? "",
                principal.FindFirst(ClaimTypes.GivenName)?.Value ?? "", projectId, principal.FindFirst(ProjectCodeClaim)?.Value, sessionId);
        }

        /// <summary>SA đăng nhập /cpanel (null nếu không)</summary>
        public static async Task<PortalIdentity?> CpanelAsync(HttpContext context)
        {
            AuthenticateResult result = await context.AuthenticateAsync(CpanelScheme);
            PortalIdentity? identity = result.Succeeded ? ToIdentity(result.Principal) : null;
            return identity is { IsSuperAdmin: true } ? identity : null;
        }

        /// <summary>Danh tính cho trang /{ma_da}: Admin / user Api của đúng dự án này; không có thì SA (thao tác thay Admin)</summary>
        public static async Task<PortalIdentity?> ProjectAsync(HttpContext context, string maDa)
        {
            AuthenticateResult result = await context.AuthenticateAsync(ProjectScheme);
            PortalIdentity? identity = result.Succeeded ? ToIdentity(result.Principal) : null;
            if (identity != null && string.Equals(identity.MaDa, maDa, StringComparison.OrdinalIgnoreCase))
            {
                return identity;
            }
            return await CpanelAsync(context);
        }
    }

    /// <summary>
    /// Kiểm cookie ở MỌI request: phiên còn hiệu lực (chưa thu hồi, chưa hết hạn), tài khoản còn hoạt động, dấu vết mật khẩu còn khớp,
    /// Admin vẫn còn được gán dự án. Không đạt thì từ chối cookie và đăng xuất
    /// </summary>
    public sealed class PortalCookieEvents(string scheme) : CookieAuthenticationEvents
    {
        public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
        {
            PortalIdentity? identity = PortalAuth.ToIdentity(context.Principal);
            string? stamp = context.Principal?.FindFirst(PortalAuth.StampClaim)?.Value;
            bool valid = false;
            if (identity != null && !string.IsNullOrEmpty(stamp))
            {
                try
                {
                    IServiceProvider services = context.HttpContext.RequestServices;
                    SessionService sessions = services.GetRequiredService<SessionService>();
                    IPortalStore store = services.GetRequiredService<IPortalStore>();
                    SessionRecord? session = await sessions.ValidateAsync(identity.SessionId, context.HttpContext.RequestAborted);
                    string? hash = null;
                    if (session != null && session.AccountType == identity.Kind && session.AccountId == identity.AccountId)
                    {
                        if (identity.IsApi)
                        {
                            PortalApiUser? user = await store.FindApiUserAsync(identity.AccountId, context.HttpContext.RequestAborted);
                            hash = user is { Active: true } && user.ProjectId == identity.ProjectId ? user.PasswordHash : null;
                        }
                        else
                        {
                            PortalAccount? account = await store.FindAccountAsync(identity.AccountId, context.HttpContext.RequestAborted);
                            bool stillAdmin = !identity.IsAdmin || identity.ProjectId != null
                                && await store.IsProjectAdminAsync(identity.AccountId, identity.ProjectId.Value, context.HttpContext.RequestAborted);
                            hash = account is { Active: true } && account.Kind == identity.Kind && stillAdmin ? account.PasswordHash : null;
                        }
                    }
                    valid = hash != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stamp), Encoding.ASCII.GetBytes(PortalAuth.StampOf(hash)));
                }
                catch (InvalidOperationException)
                {
                    valid = false;
                }
            }

            if (!valid)
            {
                if (identity != null)
                {
                    context.HttpContext.Items[PortalAuth.RejectedSessionItem] = identity.SessionId;
                }
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(scheme);
            }
        }
    }
}
