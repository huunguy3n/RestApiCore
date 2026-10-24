using ApiDocs.ControlPlane;
using Models;

namespace Tests
{
    /// <summary>
    /// Màn hình audit và dashboard tổng quan: phạm vi theo quyền (trung tâm thấy hết, quản trị một dự án chỉ thấy dự án đó,
    /// khách hàng / deployment không thấy gì), bộ lọc được làm sạch, số liệu đếm đúng từ dữ liệu
    /// </summary>
    public sealed class AuditViewTests
    {
        private const int Central = 1, ProjectAdminA = 2, Deployment = 3, CustomerA = 4, CustomerB = 5;
        private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0);

        private sealed class FixedTime(DateTime now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        }

        private static (AuditViewService Service, ControlPlaneFakeStore Store) Build()
        {
            ControlPlaneFakeStore store = new() { Projects = [new ProjectInfo(1, "A", "Dự án A"), new ProjectInfo(2, "B", "Dự án B")] };
            store.AllProjects.AddRange([new ProjectDetail(1, "A", "Dự án A", "https://a.example", true), new ProjectDetail(2, "B", "Dự án B", "https://b.example", true)]);
            store.Permissions["administrator"] = [PortalPermissions.UsersRead, PortalPermissions.AuditRead, PortalPermissions.SettingsManage];
            store.Permissions["deployment"] = [PortalPermissions.FormsRead, PortalPermissions.FormsEdit];
            store.Permissions["partner"] = [PortalPermissions.FormsRead];

            void Account(int id, string login, string kind, bool active, params MembershipRow[] rows)
            {
                store.Accounts[id] = new PortalAccount(id, login, login, kind, active, "x");
                store.Memberships[id] = rows;
            }
            MembershipRow M(int id, int account, int? project, string role, string scope) => new(id, account, project, role, scope, null);

            Account(Central, "trungtam", AccountKind.Staff, true, M(1, Central, null, "administrator", "staff"));
            Account(ProjectAdminA, "qtv_a", AccountKind.Staff, true, M(2, ProjectAdminA, 1, "administrator", "staff"));
            Account(Deployment, "trienkhai", AccountKind.Staff, true, M(3, Deployment, 1, "deployment", "staff"));
            Account(CustomerA, "khach_a", AccountKind.Customer, true, M(4, CustomerA, 1, "partner", "customer"));
            Account(CustomerB, "khach_b", AccountKind.Customer, false, M(5, CustomerB, 2, "partner", "customer"));

            // Dự án A, dự án B, và các dòng không thuộc dự án (đăng nhập); một dòng quá 7 ngày
            store.AddAudit(new AuditRecord(ProjectAdminA, 1, "account.create", "khach_a", "SUCCESS"), Now.AddHours(-1));
            store.AddAudit(new AuditRecord(ProjectAdminA, 2, "account.lock", "khach_b", "DENIED"), Now.AddHours(-2));
            store.AddAudit(new AuditRecord(Central, 2, "account.lock", "khach_b", "SUCCESS"), Now.AddHours(-3));
            store.AddAudit(new AuditRecord(null, null, "login", "ke_la", "DENIED"), Now.AddHours(-4));
            store.AddAudit(new AuditRecord(Central, null, "login", "trungtam", "SUCCESS"), Now.AddHours(-5));
            store.AddAudit(new AuditRecord(ProjectAdminA, 1, "membership.add", "khach_a", "DENIED"), Now.AddDays(-3));
            store.AddAudit(new AuditRecord(Central, 2, "project.update", "B", "SUCCESS"), Now.AddDays(-30));

            return (new AuditViewService(store, new ProjectAccessService(store), new FixedTime(Now)), store);
        }

        private static AuditFilterInput Filter(int? project = null, string? person = null, string? action = null, string? outcome = null, DateTime? from = null, DateTime? to = null) =>
            new(from, to, project, person, action, outcome);

        private static async Task<AuditView> View(AuditViewService service, int actor, AuditFilterInput filter) =>
            (AuditView)(await service.ListAsync(actor, filter, 1)).Data!;

        // ---------- Danh sách audit ----------
        [Fact]
        public async Task Central_sees_all_rows_in_the_default_window_including_login_events_without_a_project()
        {
            var (service, _) = Build();

            AuditView view = await View(service, Central, Filter());

            Assert.Equal(6, view.Page.Total);   // 7 dòng trừ dòng 30 ngày trước
            Assert.Contains(view.Page.Items, e => e.Action == "login" && e.ProjectId == null);
            Assert.Equal("account.create", view.Page.Items[0].Action);   // mới nhất trước
            Assert.Equal("qtv_a", view.Page.Items[0].ActorLogin);
            Assert.Equal(2, view.ProjectOptions.Count);
        }

        [Fact]
        public async Task Filters_by_project_person_action_outcome_and_date_range()
        {
            var (service, _) = Build();

            AuditView byProject = await View(service, Central, Filter(project: 2));
            Assert.Equal(2, byProject.Page.Total);
            Assert.All(byProject.Page.Items, e => Assert.Equal(2, e.ProjectId));

            Assert.Equal(3, (await View(service, Central, Filter(person: "qtv_a"))).Page.Total);
            Assert.Equal(1, (await View(service, Central, Filter(person: "ke_la"))).Page.Total);   // khớp cả đối tượng của lần đăng nhập sai
            Assert.Equal(2, (await View(service, Central, Filter(action: "login"))).Page.Total);
            Assert.Equal(3, (await View(service, Central, Filter(action: "account."))).Page.Total);
            Assert.Equal(3, (await View(service, Central, Filter(outcome: "denied"))).Page.Total);

            Assert.Equal(7, (await View(service, Central, Filter(from: Now.Date.AddDays(-40), to: Now.Date))).Page.Total);
            AuditView old = await View(service, Central, Filter(from: Now.Date.AddDays(-40), to: Now.Date.AddDays(-10)));
            Assert.Equal("project.update", Assert.Single(old.Page.Items).Action);
        }

        [Fact]
        public async Task Filter_text_is_sanitised_and_bad_ranges_or_outcomes_are_rejected()
        {
            var (service, _) = Build();

            // Ký tự đặc biệt bị loại: "q%v" thành "qv" (không phải ký tự đại diện khớp qtv_a)
            Assert.Equal(0, (await View(service, Central, Filter(person: "q%v"))).Page.Total);
            Assert.Equal(0, (await View(service, Central, Filter(person: "x'; DROP TABLE portalAudit;--"))).Page.Total);

            Assert.Equal("INVALID_INPUT", (await service.ListAsync(Central, Filter(from: Now.Date, to: Now.Date.AddDays(-1)), 1)).Code);
            Assert.Equal("INVALID_INPUT", (await service.ListAsync(Central, Filter(from: Now.Date.AddDays(-200), to: Now.Date), 1)).Code);
            Assert.Equal("INVALID_INPUT", (await service.ListAsync(Central, Filter(outcome: "HACK"), 1)).Code);
        }

        [Fact]
        public async Task Project_scoped_administrator_sees_only_own_project_rows_and_cannot_pick_another_project()
        {
            var (service, _) = Build();

            AuditView view = await View(service, ProjectAdminA, Filter());

            Assert.Equal(2, view.Page.Total);
            Assert.All(view.Page.Items, e => Assert.Equal(1, e.ProjectId));   // không có dòng đăng nhập (không dự án), không có dự án B
            Assert.Equal("A", Assert.Single(view.ProjectOptions).Code);
            Assert.Equal("FORBIDDEN", (await service.ListAsync(ProjectAdminA, Filter(project: 2), 1)).Code);
        }

        [Fact]
        public async Task Deployment_customers_locked_and_unknown_accounts_see_no_audit()
        {
            var (service, _) = Build();

            foreach (int actor in new[] { Deployment, CustomerA, CustomerB, 999 })
            {
                Assert.Equal("FORBIDDEN", (await service.ListAsync(actor, Filter(), 1)).Code);
            }
        }

        // ---------- Dashboard ----------
        [Fact]
        public async Task Central_dashboard_counts_come_from_the_data()
        {
            var (service, _) = Build();

            Dashboard d = (Dashboard)(await service.GetDashboardAsync(Central)).Data!;

            Assert.Equal(new AccountCounts(3, 0, 1, 1), d.Accounts);
            Assert.Equal(3, d.Denied7Days);        // lock B, đăng nhập ke_la, membership.add; dòng 30 ngày trước không tính
            Assert.Equal(1, d.FailedLogins24Hours);
            Assert.Equal(["A", "B"], d.Projects.Select(p => p.Code));
            ProjectStat a = d.Projects.Single(p => p.Code == "A");
            Assert.Equal((1, 2, 1), (a.ActiveCustomers, a.ActiveStaff, a.Denied7Days));
            ProjectStat b = d.Projects.Single(p => p.Code == "B");
            Assert.Equal((0, 0, 1), (b.ActiveCustomers, b.ActiveStaff, b.Denied7Days));   // khách của B đang bị khóa
            Assert.Equal(6, d.Recent!.Count);
            Assert.Equal("account.create", d.Recent[0].Action);
            Assert.True(d.Central);
        }

        [Fact]
        public async Task Project_scoped_dashboard_is_limited_to_its_own_project_and_hides_global_login_failures()
        {
            var (service, _) = Build();

            Dashboard d = (Dashboard)(await service.GetDashboardAsync(ProjectAdminA)).Data!;

            Assert.Equal(new AccountCounts(2, 0, 1, 0), d.Accounts);   // chỉ tài khoản có membership ở A
            Assert.Equal(1, d.Denied7Days);
            Assert.Null(d.FailedLogins24Hours);
            Assert.Equal("A", Assert.Single(d.Projects).Code);
            Assert.All(d.Recent!, e => Assert.Equal(1, e.ProjectId));
            Assert.False(d.Central);
        }

        [Fact]
        public async Task Customer_and_deployment_dashboards_contain_nothing_from_the_portal_tables()
        {
            var (service, _) = Build();

            foreach (int actor in new[] { CustomerA, Deployment })
            {
                Dashboard d = (Dashboard)(await service.GetDashboardAsync(actor)).Data!;
                Assert.Null(d.Accounts);
                Assert.Null(d.Denied7Days);
                Assert.Null(d.FailedLogins24Hours);
                Assert.Null(d.Recent);
                Assert.Empty(d.Projects);
            }
            Assert.Equal("FORBIDDEN", (await service.GetDashboardAsync(999)).Code);
        }
    }
}
