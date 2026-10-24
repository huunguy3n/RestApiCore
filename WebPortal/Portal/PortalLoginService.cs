using Security;

namespace WebPortal.Portal
{
    public sealed record LoginOutcome(string Code, VerifiedAccount? Account = null)
    {
        public bool Success => Code == "SUCCESS";
    }

    /// <summary>
    /// Xác thực mật khẩu (2 ô user/pass). Không lộ tài khoản có tồn tại hay không: cùng mã lỗi, cùng thời gian (băm giả cho tên lạ), tên lạ cũng bị đếm.
    /// /cpanel: chỉ SA. /{ma_da}: dự án phải có trong portal và nbdmda.status = 1; tra Admin của dự án trước, rồi user Api của dự án
    /// </summary>
    public sealed class PortalLoginService(IPortalStore store, IQldaDirectory qlda, LoginThrottle throttle)
    {
        /// <summary>Dự án mở đăng nhập được: có trong portal, đang hoạt động và nbdmda.status = 1. Null = 404</summary>
        public async Task<PortalProject?> FindOpenProjectAsync(string? maDa, CancellationToken cancellationToken = default)
        {
            if (!ProjectCode.IsValid(maDa))
            {
                return null;
            }
            PortalProject? project = await store.FindProjectByCodeAsync(maDa!, cancellationToken);
            if (project is not { Active: true })
            {
                return null;
            }
            QldaProject? registered = await qlda.FindAsync(project.MaDa, cancellationToken);
            return registered is { Active: true } ? project : null;
        }

        public async Task<LoginOutcome> LoginCpanelAsync(string? login, string? password, string? ip, CancellationToken cancellationToken = default)
        {
            if (!Shape(ref login, password))
            {
                return new LoginOutcome("INVALID_CREDENTIAL");
            }
            string key = "cpanel:" + login;
            if (throttle.IsLocked(key, ip))
            {
                return new LoginOutcome("ACCOUNT_LOCKED");
            }
            PortalAccount? account = await store.FindAccountByLoginAsync(login!, cancellationToken);
            bool ok = account is { Kind: AccountKind.SuperAdmin, Active: true } ? PortalPasswordHasher.Verify(password!, account.PasswordHash) : Burn(password!);
            return await FinishAsync(ok, key, ip, ok ? new VerifiedAccount(AccountKind.SuperAdmin, account!.Id, account.Login, account.DisplayName, null, null) : null,
                account?.Id, null, login!, cancellationToken);
        }

        public async Task<LoginOutcome> LoginProjectAsync(PortalProject project, string? login, string? password, string? ip, CancellationToken cancellationToken = default)
        {
            if (!Shape(ref login, password))
            {
                return new LoginOutcome("INVALID_CREDENTIAL");
            }
            string key = project.MaDa + ":" + login;
            if (throttle.IsLocked(key, ip))
            {
                return new LoginOutcome("ACCOUNT_LOCKED");
            }

            PortalAccount? admin = await store.FindAccountByLoginAsync(login!, cancellationToken);
            if (admin is { Kind: AccountKind.Admin, Active: true } && await store.IsProjectAdminAsync(admin.Id, project.Id, cancellationToken))
            {
                bool ok = PortalPasswordHasher.Verify(password!, admin.PasswordHash);
                return await FinishAsync(ok, key, ip, ok ? new VerifiedAccount(AccountKind.Admin, admin.Id, admin.Login, admin.DisplayName, project.Id, project.MaDa) : null,
                    admin.Id, project.Id, login!, cancellationToken);
            }

            PortalApiUser? user = await store.FindApiUserByNameAsync(project.Id, login!, cancellationToken);
            bool apiOk = user is { Active: true } ? PortalPasswordHasher.Verify(password!, user.PasswordHash) : Burn(password!);
            return await FinishAsync(apiOk, key, ip,
                apiOk ? new VerifiedAccount(AccountKind.Api, user!.Id, user.UserName, user.DisplayName ?? user.UserName, project.Id, project.MaDa) : null,
                null, project.Id, login!, cancellationToken);
        }

        /// <summary>Kiểm lại mật khẩu của người đang đăng nhập (xem private_key, đổi mật khẩu)</summary>
        public async Task<bool> ReverifyAsync(PortalIdentity identity, string? password, string? ip, CancellationToken cancellationToken = default)
        {
            string key = "reverify:" + identity.Kind + ":" + identity.AccountId;
            if (string.IsNullOrEmpty(password) || password.Length > 256 || throttle.IsLocked(key, ip))
            {
                return false;
            }
            string? hash = identity.IsApi
                ? (await store.FindApiUserAsync(identity.AccountId, cancellationToken))?.PasswordHash
                : (await store.FindAccountAsync(identity.AccountId, cancellationToken))?.PasswordHash;
            bool ok = hash != null && PortalPasswordHasher.Verify(password, hash);
            if (ok)
            {
                throttle.Reset(key, ip);
            }
            else
            {
                throttle.RecordFailure(key, ip);
            }
            return ok;
        }

        private async Task<LoginOutcome> FinishAsync(bool ok, string key, string? ip, VerifiedAccount? account, int? actorId, int? projectId, string login, CancellationToken cancellationToken)
        {
            if (!ok)
            {
                throttle.RecordFailure(key, ip);
                await store.WriteAuditAsync(new AuditRecord(null, actorId, NoControl(login), projectId, "login", NoControl(login), "DENIED", IpAddress: ip), cancellationToken);
                return new LoginOutcome("INVALID_CREDENTIAL");
            }
            throttle.Reset(key, ip);
            if (account!.Kind != AccountKind.Api)
            {
                await store.TouchAccountLoginAsync(account.AccountId, cancellationToken);
            }
            return new LoginOutcome("SUCCESS", account);
        }

        private static bool Shape(ref string? login, string? password)
        {
            login = (login ?? "").Trim();
            return login.Length is > 0 and <= 64 && !string.IsNullOrEmpty(password) && password.Length <= 256;
        }

        private static bool Burn(string password)
        {
            PortalPasswordHasher.BurnTime(password);
            return false;
        }

        public static string NoControl(string value) => new(value.Where(c => !char.IsControl(c)).ToArray());
    }
}
