using Microsoft.Extensions.Logging;
using Models;

namespace Business
{
    /// <summary>Vai trò và quyền hiệu lực của một tài khoản portal. Không có dòng nào = Partner, không quyền quản trị</summary>
    public sealed record PortalAccess(int UId, IReadOnlySet<string> Roles, IReadOnlySet<string> Permissions)
    {
        public bool Has(string permission) => Permissions.Contains(permission);
    }

    public sealed record AuditEvent(int? ActorUId, string ActorName, string Action, string TargetType, string Target, string Outcome, string? RequestId, string? Detail = null);

    public sealed record PortalUserSummary(int UId, string Name, string NameErp, bool Active, bool HasKey, IReadOnlyList<string> Roles);

    public sealed record PortalUserPage(int Total, IReadOnlyList<PortalUserSummary> Items);

    public sealed record UserFormAccess(int UId, string Name, bool ErpAdmin, IReadOnlyList<string> Forms);

    /// <summary>Cổng lưu trữ cho quản trị portal (DataAccess.PortalAccessRepository)</summary>
    public interface IPortalAccessRepository
    {
        /// <summary>Vai trò và quyền thô của u_id; bảng chưa tạo thì trả rỗng (deny)</summary>
        Task<(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions)> GetRolesAsync(int uId, CancellationToken cancellationToken = default);

        Task<PortalUserPage> ListUsersAsync(string search, int page, int pageSize, CancellationToken cancellationToken = default);

        Task<UserFormAccess?> GetUserFormsAsync(string userName, CancellationToken cancellationToken = default);

        /// <summary>Thay toàn bộ restApiAllowForm của user trong một transaction</summary>
        Task ReplaceUserFormsAsync(int uId, IReadOnlyCollection<string> forms, CancellationToken cancellationToken = default);

        Task WriteAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
    }

    /// <summary>Xóa cache khóa/quyền sau khi đổi để có hiệu lực ngay (SignerKeyStore)</summary>
    public interface ISignerCacheInvalidator
    {
        ValueTask InvalidateAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Catalog form đang chạy (FormRuntime). Interface để Business không phụ thuộc Api</summary>
    public interface IFormCatalogProvider
    {
        ImportFormCatalog Catalog { get; }
    }

    /// <summary>
    /// Tính quyền portal từ vai trò đã gán. Deny mặc định: chỉ quyền nằm trong PortalPermissions.All mới được tính.
    /// userinfo2.admin (ERP) cố ý không tham gia: admin FBO không tự thành Administrator portal
    /// </summary>
    public sealed class PortalAuthorizationService(IPortalAccessRepository repository)
    {
        public async Task<PortalAccess> GetAccessAsync(int uId, CancellationToken cancellationToken = default)
        {
            var (roles, permissions) = await repository.GetRolesAsync(uId, cancellationToken);
            return new PortalAccess(uId,
                new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(permissions.Where(PortalPermissions.All.Contains), StringComparer.Ordinal));
        }
    }

    public sealed record ServiceResult(string Code, object? Data = null)
    {
        public bool Success => Code == "SUCCESS";
    }

    /// <summary>
    /// Quản trị quyền form (restApiAllowForm) từ portal. Giữ nguyên ngữ nghĩa API: user không có dòng nào = MỌI form,
    /// nên xóa nốt form cuối sẽ mở rộng quyền. Vì vậy danh sách rỗng bị từ chối (EMPTY_ALLOW_LIST), không có cách mở rộng ngầm qua portal
    /// </summary>
    public sealed class FormPermissionService(IPortalAccessRepository repository, PortalAuthorizationService authorization, IFormCatalogProvider catalog,
        ISignerCacheInvalidator invalidator, ILogger<FormPermissionService> logger)
    {
        public const string ReadAction = "forms.permissions.read";
        public const string SetAction = "forms.permissions.set";

        public async Task<ServiceResult> GetAsync(int actorUId, string actorName, string targetUser, string? requestId, CancellationToken cancellationToken = default)
        {
            PortalAccess actor = await authorization.GetAccessAsync(actorUId, cancellationToken);
            if (!actor.Has(PortalPermissions.FormsPermissionsManage) && !actor.Has(PortalPermissions.UsersRead))
            {
                await AuditAsync(actorUId, actorName, ReadAction, targetUser, "DENIED", requestId, null, cancellationToken);
                return new ServiceResult("FORBIDDEN");
            }

            UserFormAccess? target = await repository.GetUserFormsAsync(targetUser, cancellationToken);
            if (target == null)
            {
                return new ServiceResult("NOT_FOUND");
            }

            // Cách quyền được suy ra: privileged (admin FBO) > explicit (có dòng) > unrestricted (không dòng = mọi form)
            string mode = target.ErpAdmin ? "erpAdmin" : target.Forms.Count > 0 ? "explicit" : "unrestricted";
            return new ServiceResult("SUCCESS", new
            {
                user = target.Name,
                mode,
                forms = target.Forms,
                broadAccess = mode != "explicit",
                warning = mode switch
                {
                    "erpAdmin" => "Tài khoản nối với NSD FBO admin: được mọi form bất kể danh sách.",
                    "unrestricted" => "Chưa khai form nào: tài khoản được MỌI form. Khai danh sách để giới hạn.",
                    _ => null
                }
            });
        }

        public async Task<ServiceResult> SetAsync(int actorUId, string actorName, string targetUser, IReadOnlyCollection<string>? forms, string? requestId, CancellationToken cancellationToken = default)
        {
            PortalAccess actor = await authorization.GetAccessAsync(actorUId, cancellationToken);
            if (!actor.Has(PortalPermissions.FormsPermissionsManage))
            {
                await AuditAsync(actorUId, actorName, SetAction, targetUser, "DENIED", requestId, null, cancellationToken);
                return new ServiceResult("FORBIDDEN");
            }

            UserFormAccess? target = await repository.GetUserFormsAsync(targetUser, cancellationToken);
            if (target == null)
            {
                return new ServiceResult("NOT_FOUND");
            }

            if (forms == null || forms.Count == 0)
            {
                await AuditAsync(actorUId, actorName, SetAction, targetUser, "REJECTED", requestId, "empty list", cancellationToken);
                return new ServiceResult("EMPTY_ALLOW_LIST");
            }

            // Mọi tên phải là form đang chạy trong catalog; lấy tên chuẩn của catalog (không tin chữ hoa/thường của client)
            Dictionary<string, string> active = catalog.Catalog.Forms.Where(f => f.Enable).ToDictionary(f => f.Name, f => f.Name, StringComparer.OrdinalIgnoreCase);
            List<string> normalized = [];
            foreach (string form in forms)
            {
                if (!active.TryGetValue((form ?? "").Trim(), out string? name))
                {
                    await AuditAsync(actorUId, actorName, SetAction, targetUser, "REJECTED", requestId, "unknown form", cancellationToken);
                    return new ServiceResult("INVALID_FORM");
                }
                if (!normalized.Contains(name, StringComparer.Ordinal))
                {
                    normalized.Add(name);
                }
            }

            await repository.ReplaceUserFormsAsync(target.UId, normalized, cancellationToken);
            await invalidator.InvalidateAsync(cancellationToken);
            await AuditAsync(actorUId, actorName, SetAction, targetUser, "SUCCESS", requestId, $"{normalized.Count} form: {string.Join(",", normalized)}", cancellationToken);
            logger.LogInformation("{Actor} đặt quyền form của {Target}: {Count} form", actorName, targetUser, normalized.Count);
            return new ServiceResult("SUCCESS", new { user = target.Name, forms = normalized });
        }

        private Task AuditAsync(int actorUId, string actorName, string action, string target, string outcome, string? requestId, string? detail, CancellationToken cancellationToken) =>
            repository.WriteAuditAsync(new AuditEvent(actorUId, actorName, action, "user", target, outcome, requestId, detail), cancellationToken);
    }

    /// <summary>Danh sách user cho trang quản trị: không bao giờ chứa mật khẩu hay private_key</summary>
    public sealed class UserAdminService(IPortalAccessRepository repository, PortalAuthorizationService authorization)
    {
        public const int MaxPageSize = 100;

        public async Task<ServiceResult> ListAsync(int actorUId, string actorName, string? search, int page, int pageSize, string? requestId, CancellationToken cancellationToken = default)
        {
            PortalAccess actor = await authorization.GetAccessAsync(actorUId, cancellationToken);
            if (!actor.Has(PortalPermissions.UsersRead))
            {
                await repository.WriteAuditAsync(new AuditEvent(actorUId, actorName, "users.list", "user", "*", "DENIED", requestId), cancellationToken);
                return new ServiceResult("FORBIDDEN");
            }

            // Chỉ giữ ký tự tên đăng nhập hợp lệ: không để % [ _ thành mẫu LIKE tùy ý
            string clean = new((search ?? "").Trim().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@').ToArray());
            PortalUserPage result = await repository.ListUsersAsync(clean.Length > 32 ? clean[..32] : clean, Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize), cancellationToken);
            return new ServiceResult("SUCCESS", result);
        }
    }
}
