using Provider;
using Security;

namespace WebPortal.Portal
{
    public sealed record AdminView(PortalAccount Account, IReadOnlyList<PortalProject> Projects);

    /// <summary>
    /// Tài khoản SA / ADMIN. Chỉ SA cấp tài khoản Admin dự án (cấp 2); Admin cấp user Api (cấp 3) ở ApiUserService.
    /// SA là duy nhất: chỉ tạo được một lần (trang khởi tạo, chỉ từ máy chủ) và không khóa được
    /// </summary>
    public sealed class AccountService(IPortalStore store, SessionService sessions, PortalLoginService login)
    {
        /// <summary>Khởi tạo SA đầu tiên. Chỉ khi chưa có SA nào</summary>
        public async Task<OpResult> CreateSuperAdminAsync(string? userName, string? displayName, string? password, string? ip, CancellationToken cancellationToken = default)
        {
            if (await store.SuperAdminExistsAsync(cancellationToken))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            if (!LoginName.IsValid(userName) || (password ?? "").Length is < LoginName.MinPasswordLength or > 256)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            string? schema = await store.CheckSchemaAsync(cancellationToken);
            if (schema != null)
            {
                return new OpResult("SCHEMA_MISSING", schema);
            }
            try
            {
                int? id = await store.CreateAccountAsync(userName!, Name(displayName, userName!), AccountKind.SuperAdmin, PortalPasswordHasher.Hash(password!), null, cancellationToken);
                if (id == null)
                {
                    return OpResult.Fail("DUPLICATE");
                }
                // SA tự đặt mật khẩu ở bước khởi tạo: không bắt đổi lại
                await store.SetAccountPasswordAsync(id.Value, PortalPasswordHasher.Hash(password!), false, cancellationToken);
                await store.WriteAuditAsync(new AuditRecord(AccountKind.SuperAdmin, id, userName, null, "sa.bootstrap", userName, "SUCCESS", IpAddress: ip), cancellationToken);
                return OpResult.Ok;
            }
            catch (DatabaseException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException sql && sql.Number == 208)
            {
                return new OpResult("SCHEMA_MISSING", "PortalDb thiếu bảng (Invalid object). Chạy sql/portal/001_portalDb.sql rồi 002_portalCustomize.sql.");
            }
        }

        public async Task<OpResult> ListAdminsAsync(PortalIdentity actor, CancellationToken cancellationToken = default)
        {
            if (!actor.IsSuperAdmin)
            {
                return OpResult.Fail("FORBIDDEN");
            }
            IReadOnlyList<PortalProject> projects = await store.ListProjectsAsync(cancellationToken);
            List<AdminView> admins = [];
            foreach (PortalAccount account in await store.ListAdminsAsync(cancellationToken))
            {
                HashSet<int> ids = [.. await store.GetAdminProjectIdsAsync(account.Id, cancellationToken)];
                admins.Add(new AdminView(account, projects.Where(p => ids.Contains(p.Id)).ToList()));
            }
            return new OpResult("SUCCESS", admins);
        }

        public async Task<OpResult> CreateAdminAsync(PortalIdentity actor, string? userName, string? displayName, string? password, IReadOnlyCollection<int> projectIds, string? ip,
            CancellationToken cancellationToken = default)
        {
            if (!actor.IsSuperAdmin)
            {
                return OpResult.Fail("FORBIDDEN");
            }
            userName = (userName ?? "").Trim();
            if (!LoginName.IsValid(userName) || (password ?? "").Length is < LoginName.MinPasswordLength or > 256)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            int? id = await store.CreateAccountAsync(userName, Name(displayName, userName), AccountKind.Admin, PortalPasswordHasher.Hash(password!), actor.AccountId, cancellationToken);
            if (id == null)
            {
                return OpResult.Fail("DUPLICATE");
            }
            foreach (int projectId in projectIds.Distinct())
            {
                if (await store.FindProjectAsync(projectId, cancellationToken) != null)
                {
                    await store.AddProjectAdminAsync(projectId, id.Value, actor.AccountId, cancellationToken);
                }
            }
            await store.WriteAuditAsync(PortalGuard.Audit(actor, null, "admin.create", userName, "SUCCESS", $"{projectIds.Count} dự án", ip), cancellationToken);
            return new OpResult("SUCCESS", id.Value);
        }

        public async Task<OpResult> SetProjectAsync(PortalIdentity actor, int adminId, int projectId, bool assign, string? ip, CancellationToken cancellationToken = default)
        {
            PortalAccount? admin = actor.IsSuperAdmin ? await store.FindAccountAsync(adminId, cancellationToken) : null;
            PortalProject? project = admin == null ? null : await store.FindProjectAsync(projectId, cancellationToken);
            if (admin is not { Kind: AccountKind.Admin } || project == null)
            {
                return OpResult.Fail(actor.IsSuperAdmin ? "NOT_FOUND" : "FORBIDDEN");
            }
            if (assign)
            {
                await store.AddProjectAdminAsync(projectId, adminId, actor.AccountId, cancellationToken);
            }
            else
            {
                await store.RemoveProjectAdminAsync(projectId, adminId, cancellationToken);
                // Phiên đang mở ở dự án này bị cookie events từ chối ở request kế tiếp (không còn là Admin của dự án)
            }
            await store.WriteAuditAsync(PortalGuard.Audit(actor, projectId, assign ? "admin.project.assign" : "admin.project.remove", admin.Login, "SUCCESS", null, ip), cancellationToken);
            return OpResult.Ok;
        }

        public async Task<OpResult> SetActiveAsync(PortalIdentity actor, int adminId, bool active, string? ip, CancellationToken cancellationToken = default)
        {
            PortalAccount? admin = actor.IsSuperAdmin ? await store.FindAccountAsync(adminId, cancellationToken) : null;
            if (admin is not { Kind: AccountKind.Admin })
            {
                return OpResult.Fail(actor.IsSuperAdmin ? "NOT_FOUND" : "FORBIDDEN");
            }
            await store.SetAccountActiveAsync(adminId, active, cancellationToken);
            if (!active)
            {
                await sessions.EndAllAsync(AccountKind.Admin, adminId, "locked", cancellationToken);
            }
            await store.WriteAuditAsync(PortalGuard.Audit(actor, null, active ? "admin.unlock" : "admin.lock", admin.Login, "SUCCESS", null, ip), cancellationToken);
            return OpResult.Ok;
        }

        /// <summary>SA đặt lại mật khẩu Admin: Admin phải đổi ở lần đăng nhập sau, mọi phiên cũ bị thu hồi</summary>
        public async Task<OpResult> ResetPasswordAsync(PortalIdentity actor, int adminId, string? password, string? ip, CancellationToken cancellationToken = default)
        {
            PortalAccount? admin = actor.IsSuperAdmin ? await store.FindAccountAsync(adminId, cancellationToken) : null;
            if (admin is not { Kind: AccountKind.Admin })
            {
                return OpResult.Fail(actor.IsSuperAdmin ? "NOT_FOUND" : "FORBIDDEN");
            }
            if ((password ?? "").Length is < LoginName.MinPasswordLength or > 256)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            await store.SetAccountPasswordAsync(adminId, PortalPasswordHasher.Hash(password!), true, cancellationToken);
            await sessions.EndAllAsync(AccountKind.Admin, adminId, "password", cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, null, "admin.password.reset", admin.Login, "SUCCESS", null, ip), cancellationToken);
            return OpResult.Ok;
        }

        public async Task<OpResult> ForceLogoutAsync(PortalIdentity actor, string kind, int accountId, string? ip, CancellationToken cancellationToken = default)
        {
            if (!actor.IsSuperAdmin || kind is not (AccountKind.Admin or AccountKind.Api))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            await sessions.EndAllAsync(kind, accountId, "forced", cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, null, "session.force_logout", $"{kind}#{accountId}", "SUCCESS", null, ip), cancellationToken);
            return OpResult.Ok;
        }

        /// <summary>Tự đổi mật khẩu (SA, Admin, user Api). Thành công thì mọi phiên bị thu hồi, phải đăng nhập lại</summary>
        public async Task<OpResult> ChangeOwnPasswordAsync(PortalIdentity actor, string? current, string? next, string? ip, CancellationToken cancellationToken = default)
        {
            if ((next ?? "").Length is < LoginName.MinPasswordLength or > 256 || next == current)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            if (!await login.ReverifyAsync(actor, current, ip, cancellationToken))
            {
                await store.WriteAuditAsync(PortalGuard.Audit(actor, actor.ProjectId, "password.change", actor.Login, "DENIED", null, ip), cancellationToken);
                return OpResult.Fail("INVALID_CREDENTIAL");
            }
            string hash = PortalPasswordHasher.Hash(next!);
            if (actor.IsApi)
            {
                await store.SetApiUserPasswordAsync(actor.AccountId, hash, cancellationToken);
            }
            else
            {
                await store.SetAccountPasswordAsync(actor.AccountId, hash, false, cancellationToken);
            }
            await sessions.EndAllAsync(actor.Kind, actor.AccountId, "password", cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, actor.ProjectId, "password.change", actor.Login, "SUCCESS", null, ip), cancellationToken);
            return OpResult.Ok;
        }

        private static string Name(string? displayName, string login)
        {
            string clean = new((displayName ?? "").Trim().Where(c => !char.IsControl(c)).ToArray());
            return clean.Length == 0 ? login : clean.Length > 100 ? clean[..100] : clean;
        }
    }
}
