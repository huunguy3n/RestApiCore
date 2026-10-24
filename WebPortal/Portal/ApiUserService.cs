using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Business;
using DataAccess;
using Security;

namespace WebPortal.Portal
{
    public sealed record ApiUserInput(string? UserName, string? NameErp, string? DisplayName, string? Password, bool Active, string? Forms, string? Ips);

    /// <summary>Khóa private_key vừa cấp hoặc vừa giải: chỉ hiện một lần, không lưu ở portal</summary>
    public sealed record IssuedKey(string UserName, string PrivateKeyBase64);

    /// <summary>
    /// User Api của dự án. Admin dự án (hoặc SA) tạo/sửa/khóa user, map name_erp theo NSD FBO, cấp form/IP, cấp/xoay khóa, thu hồi token.
    /// User Api tự thu hồi token và xem khóa của chính mình (nhập lại mật khẩu). Mọi thay đổi ghi portal trước rồi đẩy một chiều xuống dự án.
    /// Quyền: có khai = đúng danh sách; rỗng = tất cả (ngữ nghĩa của WebApi)
    /// </summary>
    public sealed partial class ApiUserService(IPortalStore store, IProjectConnectionResolver resolver, ProjectSyncService sync, SessionService sessions,
        PortalLoginService login)
    {
        public const int MaxForms = ProjectAdminRepository.MaxForms;
        public const int MaxIps = ProjectAdminRepository.MaxIps;

        public async Task<OpResult> ListAsync(PortalIdentity actor, PortalProject project, CancellationToken cancellationToken = default) =>
            PortalGuard.CanAdmin(actor, project.Id) ? new OpResult("SUCCESS", await store.ListApiUsersAsync(project.Id, cancellationToken)) : OpResult.Fail("FORBIDDEN");

        /// <summary>NSD FBO đang hoạt động của dự án, để chọn name_erp</summary>
        public async Task<OpResult> ErpUsersAsync(PortalIdentity actor, PortalProject project, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            try
            {
                IReadOnlyList<ErpUser> users = await resolver.Resolve(project).Admin.ListErpUsersAsync(cancellationToken);
                return new OpResult("SUCCESS", users.Where(u => u.Active).ToList());
            }
            catch (Exception ex) when (IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ProjectMessage(ex));
            }
        }

        public async Task<OpResult> CreateAsync(PortalIdentity actor, PortalProject project, ApiUserInput input, string? ip, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return await DeniedAsync(actor, project, "apiuser.create", input.UserName, ip, cancellationToken);
            }
            string userName = (input.UserName ?? "").Trim();
            if (!LoginName.IsValid(userName) || (input.Password ?? "").Length < LoginName.MinPasswordLength || (input.Password ?? "").Length > 256)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            var (rights, rightsError) = ParseRights(input.Forms, input.Ips);
            if (rightsError != null)
            {
                return OpResult.Fail(rightsError);
            }
            OpResult erp = await CheckErpAsync(project, input.NameErp, cancellationToken);
            if (!erp.Success)
            {
                return erp;
            }

            int? id = await store.CreateApiUserAsync(project.Id, new NewApiUser(userName, (string)erp.Data!, Clean(input.DisplayName, 100),
                PortalPasswordHasher.Hash(input.Password!), rights!.Value.Forms, rights.Value.Ips), actor.AccountId, cancellationToken);
            if (id == null)
            {
                return OpResult.Fail("DUPLICATE");
            }
            OpResult pushed = await sync.PushAsync(project, id.Value, cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.create", userName, "SUCCESS",
                $"name_erp {erp.Data}; form: {Describe(rights.Value.Forms)}; IP: {Describe(rights.Value.Ips.Select(i => i.Ip).ToList())}; đồng bộ: {pushed.Code}", ip), cancellationToken);
            return new OpResult("SUCCESS", new { id = id.Value, sync = pushed });
        }

        public async Task<OpResult> UpdateAsync(PortalIdentity actor, PortalProject project, int id, ApiUserInput input, string? ip, CancellationToken cancellationToken = default)
        {
            PortalApiUser? user = await store.FindApiUserAsync(id, cancellationToken);
            if (!PortalGuard.CanAdmin(actor, project.Id) || user == null || user.ProjectId != project.Id)
            {
                return user == null || user.ProjectId != project.Id ? OpResult.Fail("NOT_FOUND") : await DeniedAsync(actor, project, "apiuser.update", user.UserName, ip, cancellationToken);
            }
            var (rights, rightsError) = ParseRights(input.Forms, input.Ips);
            if (rightsError != null)
            {
                return OpResult.Fail(rightsError);
            }
            string nameErp = user.NameErp;
            if (!string.Equals((input.NameErp ?? "").Trim(), user.NameErp, StringComparison.OrdinalIgnoreCase))
            {
                OpResult erp = await CheckErpAsync(project, input.NameErp, cancellationToken);
                if (!erp.Success)
                {
                    return erp;
                }
                nameErp = (string)erp.Data!;
            }

            await store.UpdateApiUserAsync(id, nameErp, Clean(input.DisplayName, 100), input.Active, rights!.Value.Forms, rights.Value.Ips, cancellationToken);
            if (user.Active && !input.Active)
            {
                await sessions.EndAllAsync(AccountKind.Api, id, "locked", cancellationToken);
            }
            OpResult pushed = await sync.PushAsync(project, id, cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.update", user.UserName, "SUCCESS",
                $"name_erp {nameErp}; {(input.Active ? "hoạt động" : "khóa")}; form: {Describe(rights.Value.Forms)}; IP: {Describe(rights.Value.Ips.Select(i => i.Ip).ToList())}; đồng bộ: {pushed.Code}",
                ip), cancellationToken);
            return new OpResult("SUCCESS", pushed);
        }

        public async Task<OpResult> ResetPasswordAsync(PortalIdentity actor, PortalProject project, int id, string? password, string? ip, CancellationToken cancellationToken = default)
        {
            PortalApiUser? user = await RequireUserAsync(actor, project, id, cancellationToken);
            if (user == null)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            if ((password ?? "").Length is < LoginName.MinPasswordLength or > 256)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            await store.SetApiUserPasswordAsync(id, PortalPasswordHasher.Hash(password!), cancellationToken);
            await sessions.EndAllAsync(AccountKind.Api, id, "password", cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.password.reset", user.UserName, "SUCCESS", null, ip), cancellationToken);
            return OpResult.Ok;
        }

        public async Task<OpResult> ResyncAsync(PortalIdentity actor, PortalProject project, int id, string? ip, CancellationToken cancellationToken = default)
        {
            PortalApiUser? user = await RequireUserAsync(actor, project, id, cancellationToken);
            if (user == null)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            OpResult pushed = await sync.PushAsync(project, id, cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.sync", user.UserName, pushed.Success ? "SUCCESS" : "FAILED", null, ip), cancellationToken);
            return pushed;
        }

        /// <summary>Cấp hoặc xoay private_key: khóa cũ hết hiệu lực ngay. Khóa mới chỉ hiện một lần cho Admin</summary>
        public async Task<OpResult> IssueKeyAsync(PortalIdentity actor, PortalProject project, int id, string? ip, CancellationToken cancellationToken = default)
        {
            PortalApiUser? user = await RequireUserAsync(actor, project, id, cancellationToken);
            if (user == null)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            OpResult pushed = await sync.PushAsync(project, id, cancellationToken);
            if (!pushed.Success)
            {
                return pushed;
            }
            try
            {
                ProjectConnection connection = resolver.Resolve(project);
                PrivateKeyProtector? protector = connection.Protector();
                if (protector == null)
                {
                    return OpResult.Fail("NO_KEY_ENCRYPTION_KEY");
                }
                byte[]? key = await connection.Admin.IssueKeyAsync(user.UserName, protector, cancellationToken);
                if (key == null)
                {
                    return OpResult.Fail("NOT_FOUND");
                }
                string base64 = Convert.ToBase64String(key);
                CryptographicOperations.ZeroMemory(key);
                await connection.Admin.WriteAuditAsync(new AuditEntry("PORTAL", "key.issue", user.UserName, "SUCCESS", Clean(actor.Login, 32), null), cancellationToken);
                await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.key.issue", user.UserName, "SUCCESS", null, ip), cancellationToken);
                return new OpResult("SUCCESS", new IssuedKey(user.UserName, base64));
            }
            catch (Exception ex) when (IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ProjectMessage(ex));
            }
        }

        /// <summary>Thu hồi mọi JWT của user (Admin thu hồi cho user, hoặc user Api tự thu hồi của mình)</summary>
        public async Task<OpResult> RevokeTokensAsync(PortalIdentity actor, PortalProject project, int id, string? ip, CancellationToken cancellationToken = default)
        {
            PortalApiUser? user = PortalGuard.IsApiOf(actor, project.Id) && actor.AccountId == id
                ? await store.FindApiUserAsync(id, cancellationToken)
                : await RequireUserAsync(actor, project, id, cancellationToken);
            if (user == null || user.ProjectId != project.Id)
            {
                return OpResult.Fail("NOT_FOUND");
            }
            try
            {
                ProjectConnection connection = resolver.Resolve(project);
                if (!await connection.Admin.RevokeTokensAsync(user.UserName, cancellationToken))
                {
                    return OpResult.Fail("NOT_SYNCED");
                }
                await connection.Admin.WriteAuditAsync(new AuditEntry("PORTAL", "token.revoke", user.UserName, "SUCCESS", Clean(actor.Login, 32), null), cancellationToken);
                await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.token.revoke", user.UserName, "SUCCESS", null, ip), cancellationToken);
                return OpResult.Ok;
            }
            catch (Exception ex) when (IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ProjectMessage(ex));
            }
        }

        /// <summary>User Api xem lại private_key của chính mình: bắt buộc nhập lại mật khẩu, mỗi lần xem ghi nhật ký</summary>
        public async Task<OpResult> ViewOwnKeyAsync(PortalIdentity actor, PortalProject project, string? password, string? ip, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.IsApiOf(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            if (!await login.ReverifyAsync(actor, password, ip, cancellationToken))
            {
                await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.key.view", actor.Login, "DENIED", "sai mật khẩu", ip), cancellationToken);
                return OpResult.Fail("INVALID_CREDENTIAL");
            }
            try
            {
                ProjectConnection connection = resolver.Resolve(project);
                PrivateKeyProtector? protector = connection.Protector();
                byte[]? key = protector == null ? null : await connection.Admin.ReadKeyAsync(actor.Login, protector, cancellationToken);
                if (key == null)
                {
                    return OpResult.Fail("KEY_NOT_ISSUED");
                }
                string base64 = Convert.ToBase64String(key);
                CryptographicOperations.ZeroMemory(key);
                await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "apiuser.key.view", actor.Login, "SUCCESS", null, ip), cancellationToken);
                return new OpResult("SUCCESS", new IssuedKey(actor.Login, base64));
            }
            catch (Exception ex) when (IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ProjectMessage(ex));
            }
        }

        /// <summary>Trạng thái user trên DB dự án (có khóa chưa, thời điểm thu hồi token) — cho màn user Api và Admin</summary>
        public async Task<ProjectApiUser?> RemoteStatusAsync(PortalProject project, string userName, CancellationToken cancellationToken = default)
        {
            try
            {
                return await resolver.Resolve(project).Admin.FindUserAsync(userName, cancellationToken);
            }
            catch (Exception ex) when (IsProjectError(ex))
            {
                return null;
            }
        }

        private async Task<PortalApiUser?> RequireUserAsync(PortalIdentity actor, PortalProject project, int id, CancellationToken cancellationToken)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return null;
            }
            PortalApiUser? user = await store.FindApiUserAsync(id, cancellationToken);
            return user?.ProjectId == project.Id ? user : null;
        }

        private async Task<OpResult> CheckErpAsync(PortalProject project, string? nameErp, CancellationToken cancellationToken)
        {
            nameErp = (nameErp ?? "").Trim();
            if (nameErp.Length is 0 or > 30)
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            try
            {
                ErpUser? erp = await resolver.Resolve(project).Admin.FindErpUserAsync(nameErp, cancellationToken);
                return erp is { Active: true } ? new OpResult("SUCCESS", erp.Name) : OpResult.Fail("ERP_USER_NOT_FOUND");
            }
            catch (Exception ex) when (IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ProjectMessage(ex));
            }
        }

        /// <summary>Form: tên file XML không đuôi, cách nhau dấu phẩy/xuống dòng. IP: mỗi dòng "ip [mô tả]", "255" là wildcard. Rỗng = tất cả</summary>
        public static ((IReadOnlyList<string> Forms, IReadOnlyList<ApiUserIp> Ips)?, string? Error) ParseRights(string? forms, string? ips)
        {
            List<string> formList = (forms ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (formList.Count > MaxForms || formList.Any(f => !FormDefinitionValidator.IsValidName(f)))
            {
                return (null, "INVALID_FORM_NAME");
            }
            List<ApiUserIp> ipList = [];
            foreach (string line in (ips ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (!IpPattern().IsMatch(parts[0]))
                {
                    return (null, "INVALID_IP");
                }
                ipList.Add(new ApiUserIp(parts[0], parts.Length > 1 ? Clean(parts[1], 100) : null));
            }
            return ipList.Count > MaxIps ? (null, "INVALID_IP") : ((formList, ipList.DistinctBy(i => i.Ip).ToList()), null);
        }

        public static string Describe(IReadOnlyCollection<string> items) => items.Count == 0 ? "tất cả" : string.Join(", ", items);

        private async Task<OpResult> DeniedAsync(PortalIdentity actor, PortalProject project, string action, string? target, string? ip, CancellationToken cancellationToken)
        {
            await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, action, target, "DENIED", null, ip), cancellationToken);
            return OpResult.Fail("FORBIDDEN");
        }

        internal static bool IsProjectError(Exception ex) =>
            ex is ProjectFolderException or Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException or IOException or UnauthorizedAccessException or FormatException;

        /// <summary>Không đưa nội dung lỗi SQL ra giao diện (có thể chứa tên máy chủ/DB)</summary>
        internal static string ProjectMessage(Exception ex) => ex is ProjectFolderException ? ex.Message : "Không truy cập được DB/folder dự án.";

        private static string? Clean(string? value, int max)
        {
            string clean = new((value ?? "").Trim().Where(c => !char.IsControl(c)).ToArray());
            return clean.Length == 0 ? null : clean.Length > max ? clean[..max] : clean;
        }

        [GeneratedRegex(@"^[0-9A-Fa-f:.]{2,45}$")]
        private static partial Regex IpPattern();
    }
}
