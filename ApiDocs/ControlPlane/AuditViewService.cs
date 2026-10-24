using Models;

namespace ApiDocs.ControlPlane
{
    public sealed record AuditFilterInput(DateTime? From, DateTime? To, int? ProjectId, string? Person, string? Action, string? Outcome);

    public sealed record AuditView(AuditPage Page, IReadOnlyList<ProjectInfo> ProjectOptions, DateTime From, DateTime To);

    /// <summary>
    /// Xem audit và dashboard tổng quan, số liệu lấy thật từ bảng portal. Phạm vi theo quyền:
    /// administrator trung tâm có audit.read thấy mọi dòng (kể cả đăng nhập, không thuộc dự án); người chỉ có audit.read ở vài dự án
    /// chỉ thấy dòng của các dự án đó. Số tài khoản theo users.read, cùng quy tắc phạm vi. Khách hàng và deployment không thấy gì
    /// </summary>
    public sealed class AuditViewService(IControlPlaneStore store, ProjectAccessService access, TimeProvider time)
    {
        public const int PageSize = 50;
        public const int MaxRangeDays = 92;
        private static readonly string[] Outcomes = ["SUCCESS", "DENIED", "REJECTED"];

        public async Task<OpResult> ListAsync(int actorId, AuditFilterInput input, int page, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            IReadOnlyCollection<int>? scope = actor == null ? [] : ScopeFor(actor, PortalPermissions.AuditRead);
            if (actor == null || scope is { Count: 0 })
            {
                return new OpResult("FORBIDDEN");
            }

            DateTime today = time.GetLocalNow().Date;
            DateTime from = (input.From ?? today.AddDays(-6)).Date;
            DateTime to = (input.To ?? today).Date;
            string outcome = (input.Outcome ?? "").Trim().ToUpperInvariant();
            if (to < from || (to - from).TotalDays > MaxRangeDays || outcome.Length > 0 && !Outcomes.Contains(outcome))
            {
                return new OpResult("INVALID_INPUT");
            }
            // Dự án được chọn phải nằm trong phạm vi xem của người dùng (ngoài phạm vi = từ chối, không im lặng bỏ lọc)
            if (input.ProjectId is int chosen && scope != null && !scope.Contains(chosen))
            {
                return new OpResult("FORBIDDEN");
            }

            AuditQuery query = new(from, to.AddDays(1), input.ProjectId, Clean(input.Person, 64, "._@-"), Clean(input.Action, 40, "."), outcome);
            AuditPage result = await store.ListAuditAsync(query, Math.Max(page, 1), PageSize, scope, cancellationToken);
            IReadOnlyList<ProjectInfo> options = (await store.GetActiveProjectsAsync(cancellationToken)).Where(p => scope == null || scope.Contains(p.Id)).ToList();
            return new OpResult("SUCCESS", new AuditView(result, options, from, to));
        }

        public async Task<OpResult> GetDashboardAsync(int actorId, CancellationToken cancellationToken = default)
        {
            AccountAccess? actor = await access.GetAccessAsync(actorId, cancellationToken);
            if (actor == null)
            {
                return new OpResult("FORBIDDEN");
            }

            IReadOnlyCollection<int>? accountScope = ScopeFor(actor, PortalPermissions.UsersRead);
            IReadOnlyCollection<int>? auditScope = ScopeFor(actor, PortalPermissions.AuditRead);
            bool canAccounts = accountScope is not { Count: 0 }, canAudit = auditScope is not { Count: 0 };
            bool central = actor.CanCentral(PortalPermissions.UsersRead) || actor.CanCentral(PortalPermissions.AuditRead);

            // Thống kê theo dự án chỉ cho những dự án người xem có quyền đọc tài khoản hoặc audit (khách hàng không thấy số của khách khác)
            IReadOnlyCollection<int>? statScope = central ? null : Union(accountScope, auditScope);
            DateTime now = time.GetLocalNow().DateTime;

            AccountCounts? accounts = canAccounts ? await store.GetAccountCountsAsync(accountScope, cancellationToken) : null;
            IReadOnlyList<ProjectStat> projects = statScope is { Count: 0 } ? [] : await store.GetProjectStatsAsync(statScope, now.AddDays(-7), cancellationToken);
            // Số lần bị từ chối là dữ liệu audit: người không có audit.read thì không thấy
            if (!canAudit)
            {
                projects = projects.Select(project => project with { Denied7Days = 0 }).ToList();
            }
            int? denied = canAudit ? await store.CountAuditAsync(now.AddDays(-7), "DENIED", null, auditScope, cancellationToken) : null;
            // Đăng nhập sai không thuộc dự án nào nên chỉ có ý nghĩa với người xem được mọi dòng audit
            int? failedLogins = canAudit && auditScope == null ? await store.CountAuditAsync(now.AddDays(-1), "DENIED", "login", null, cancellationToken) : null;
            IReadOnlyList<AuditEntry>? recent = canAudit
                ? (await store.ListAuditAsync(new AuditQuery(now.Date.AddDays(-6), now.Date.AddDays(1), null, "", "", ""), 1, 8, auditScope, cancellationToken)).Items
                : null;

            return new OpResult("SUCCESS", new Dashboard(accounts, projects, denied, failedLogins, recent, central));
        }

        /// <summary>Null = mọi dự án (administrator trung tâm có quyền đó); ngược lại là các dự án có quyền (rỗng = không có quyền)</summary>
        private static IReadOnlyCollection<int>? ScopeFor(AccountAccess actor, string permission) =>
            actor.CanCentral(permission) ? null : actor.Projects.Where(p => p.Permissions.Contains(permission)).Select(p => p.Project.Id).ToList();

        private static IReadOnlyCollection<int> Union(IReadOnlyCollection<int>? a, IReadOnlyCollection<int>? b) =>
            (a ?? []).Union(b ?? []).ToList();

        /// <summary>Chỉ giữ chữ-số ASCII và ký tự cho phép; chuỗi lọc không bao giờ chứa ký tự đặc biệt của SQL/LIKE</summary>
        private static string Clean(string? value, int max, string extra)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            return new string(value.Trim().Where(c => char.IsAsciiLetterOrDigit(c) || extra.Contains(c)).Take(max).ToArray());
        }
    }
}
