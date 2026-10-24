using System.Net;
using WebPortal.ControlPlane;
using Models;
using Security;

namespace Tests
{
    /// <summary>
    /// Quản trị tài khoản / membership / dự án: phạm vi quyền (trung tâm, theo dự án, khách), ràng buộc không tự khóa /
    /// không gỡ administrator cuối, chống leo thang qua form, audit không chứa mật khẩu
    /// </summary>
    public sealed class AccountAdminTests
    {
        private const int Central = 1, ProjectAdminA = 2, Deployment = 3, CustomerA = 4, CustomerB = 5, Outsider = 6, SecondCentral = 7, CustomerBoth = 8;
        private static readonly ProjectInfo A = new(1, "A", "Dự án A");
        private static readonly ProjectInfo B = new(2, "B", "Dự án B");
        private const string StrongPassword = "MatKhau-Manh-12345";

        private static (AccountAdminService Service, ControlPlaneFakeStore Store) Build()
        {
            ControlPlaneFakeStore store = new() { Projects = [A, B] };
            store.AllProjects.AddRange([new ProjectDetail(1, "A", "Dự án A", "https://a.example", true), new ProjectDetail(2, "B", "Dự án B", "https://b.example", true)]);
            store.Permissions["administrator"] = [PortalPermissions.UsersRead, PortalPermissions.UsersCreate, PortalPermissions.UsersUpdate, PortalPermissions.UsersDeactivate,
                PortalPermissions.UsersRolesManage, PortalPermissions.SettingsManage, PortalPermissions.FormsEdit];
            store.Permissions["deployment"] = [PortalPermissions.FormsRead, PortalPermissions.FormsEdit, PortalPermissions.FormsPermissionsManage];
            store.Permissions["partner"] = [PortalPermissions.FormsRead];

            string hash = PortalPasswordHasher.Hash(StrongPassword, iterations: 20_000);
            void Account(int id, string login, string kind, params MembershipRow[] rows)
            {
                store.Accounts[id] = new PortalAccount(id, login, login, kind, true, hash);
                store.Memberships[id] = rows;
            }
            MembershipRow M(int id, int account, int? project, string role, string scope, string? apiUser = null) => new(id, account, project, role, scope, apiUser);

            Account(Central, "trungtam", AccountKind.Staff, M(11, Central, null, "administrator", "staff"));
            Account(SecondCentral, "trungtam2", AccountKind.Staff, M(12, SecondCentral, null, "administrator", "staff"));
            Account(ProjectAdminA, "qtv_a", AccountKind.Staff, M(13, ProjectAdminA, 1, "administrator", "staff"));
            Account(Deployment, "trienkhai", AccountKind.Staff, M(14, Deployment, 1, "deployment", "staff"));
            Account(CustomerA, "khach_a", AccountKind.Customer, M(15, CustomerA, 1, "partner", "customer", "KHACH_A_API"));
            Account(CustomerB, "khach_b", AccountKind.Customer, M(16, CustomerB, 2, "partner", "customer"));
            Account(CustomerBoth, "khach_ab", AccountKind.Customer, M(17, CustomerBoth, 1, "partner", "customer"), M(18, CustomerBoth, 2, "partner", "customer"));
            Account(Outsider, "khong_quyen", AccountKind.Customer);

            return (new AccountAdminService(store, new ProjectAccessService(store)), store);
        }

        // ---------- Tạo tài khoản ----------
        [Fact]
        public async Task Central_creates_customer_and_staff_and_never_stores_the_password()
        {
            var (service, store) = Build();

            OpResult customer = await service.CreateAccountAsync(Central, "khach_moi", "Khách mới", "customer", StrongPassword, 1, "partner", "KHACH_MOI_API");
            OpResult staff = await service.CreateAccountAsync(Central, "nv_moi", "Nhân viên", "staff", StrongPassword, null, "administrator", null);

            Assert.True(customer.Success && staff.Success);
            PortalAccount created = store.Accounts[(int)customer.Data!];
            Assert.NotEqual(StrongPassword, created.PasswordHash);
            Assert.True(PortalPasswordHasher.Verify(StrongPassword, created.PasswordHash));
            Assert.All(store.Audit, a => Assert.DoesNotContain(StrongPassword, (a.Target ?? "") + (a.Detail ?? "")));
            Assert.Contains(store.Audit, a => a.Action == "account.create" && a.Outcome == "SUCCESS" && a.Target == "khach_moi");
        }

        [Fact]
        public async Task Project_admin_creates_customers_only_in_own_project_and_cannot_create_staff()
        {
            var (service, _) = Build();

            Assert.True((await service.CreateAccountAsync(ProjectAdminA, "khach_a2", "Khách A2", "customer", StrongPassword, 1, "partner", null)).Success);
            Assert.Equal("FORBIDDEN", (await service.CreateAccountAsync(ProjectAdminA, "khach_b2", "Khách B2", "customer", StrongPassword, 2, "partner", null)).Code);
            Assert.Equal("FORBIDDEN", (await service.CreateAccountAsync(ProjectAdminA, "nv_x", "NV", "staff", StrongPassword, 1, "deployment", null)).Code);
            Assert.Equal("FORBIDDEN", (await service.CreateAccountAsync(ProjectAdminA, "nv_y", "NV", "staff", StrongPassword, null, "administrator", null)).Code);
        }

        [Fact]
        public async Task Deployment_customer_and_unknown_actors_cannot_create_accounts()
        {
            var (service, store) = Build();

            foreach (int actor in new[] { Deployment, CustomerA, Outsider, 999 })
            {
                OpResult result = await service.CreateAccountAsync(actor, "ke_tan_cong", "X", "customer", StrongPassword, 1, "partner", null);
                Assert.Equal("FORBIDDEN", result.Code);
            }
            Assert.DoesNotContain(store.Accounts.Values, a => a.Login == "ke_tan_cong");
        }

        [Theory]
        [InlineData("ab", "Tên", "customer", StrongPassword, 1, "partner")]                 // tên đăng nhập quá ngắn
        [InlineData("co khoang trang", "Tên", "customer", StrongPassword, 1, "partner")]    // ký tự không hợp lệ
        [InlineData("khach_x", "Tên", "customer", "ngan", 1, "partner")]                    // mật khẩu ngắn
        [InlineData("khach_x", "Tên", "customer", StrongPassword, 1, "quyen_tuy_y")]        // vai trò lạ
        [InlineData("khach_x", "Tên", "robot", StrongPassword, 1, "partner")]               // loại lạ
        public async Task Invalid_input_is_rejected_before_anything_is_written(string login, string name, string kind, string password, int project, string role)
        {
            var (service, store) = Build();
            int before = store.Accounts.Count;

            OpResult result = await service.CreateAccountAsync(Central, login, name, kind, password, project, role, null);

            Assert.Equal("INVALID_INPUT", result.Code);
            Assert.Equal(before, store.Accounts.Count);
        }

        [Fact]
        public async Task Membership_rules_block_escalation_through_the_create_form()
        {
            var (service, store) = Build();
            int before = store.Accounts.Count;

            // Khách mang vai trò staff / không dự án; deployment không dự án
            Assert.Equal("INVALID_MEMBERSHIP", (await service.CreateAccountAsync(Central, "khach_k1", "K", "customer", StrongPassword, 1, "administrator", null)).Code);
            Assert.Equal("INVALID_MEMBERSHIP", (await service.CreateAccountAsync(Central, "khach_k2", "K", "customer", StrongPassword, null, "partner", null)).Code);
            Assert.Equal("INVALID_MEMBERSHIP", (await service.CreateAccountAsync(Central, "staff_s1", "S", "staff", StrongPassword, null, "deployment", null)).Code);
            Assert.Equal("INVALID_MEMBERSHIP", (await service.CreateAccountAsync(Central, "staff_s2", "S", "staff", StrongPassword, 1, "partner", null)).Code);
            Assert.Equal("INVALID_MEMBERSHIP", (await service.CreateAccountAsync(Central, "khach_k3", "K", "customer", StrongPassword, 999, "partner", null)).Code);   // dự án không tồn tại
            Assert.Equal(before, store.Accounts.Count);
        }

        [Fact]
        public async Task Duplicate_login_is_reported_and_audited()
        {
            var (service, store) = Build();

            OpResult result = await service.CreateAccountAsync(Central, "KHACH_A", "Trùng", "customer", StrongPassword, 1, "partner", null);

            Assert.Equal("DUPLICATE", result.Code);
            Assert.Contains(store.Audit, a => a.Outcome == "REJECTED" && a.Action == "account.create");
        }

        // ---------- Danh sách / chi tiết: không dò được ngoài phạm vi ----------
        [Fact]
        public async Task Listing_is_scoped_and_hides_password_hashes()
        {
            var (service, _) = Build();

            AccountPage central = (AccountPage)(await service.ListAccountsAsync(Central, null, 1, 50)).Data!;
            AccountPage projectA = (AccountPage)(await service.ListAccountsAsync(ProjectAdminA, null, 1, 50)).Data!;

            Assert.Equal(8, central.Total);
            Assert.DoesNotContain(projectA.Items, a => a.Login == "khach_b");
            Assert.Contains(projectA.Items, a => a.Login == "khach_a");
            Assert.Contains(projectA.Items, a => a.Login == "khach_ab");   // có membership ở A
            Assert.Equal(typeof(AccountSummary), central.Items[0].GetType());   // kiểu hiển thị không có băm mật khẩu

            Assert.Equal("FORBIDDEN", (await service.ListAccountsAsync(Deployment, null, 1, 50)).Code);
            Assert.Equal("FORBIDDEN", (await service.ListAccountsAsync(CustomerA, null, 1, 50)).Code);
        }

        [Fact]
        public async Task Detail_outside_scope_looks_like_not_found()
        {
            var (service, _) = Build();

            Assert.Equal("NOT_FOUND", (await service.GetDetailAsync(ProjectAdminA, CustomerB)).Code);
            Assert.Equal("NOT_FOUND", (await service.GetDetailAsync(Deployment, CustomerA)).Code);
            Assert.Equal("NOT_FOUND", (await service.GetDetailAsync(Central, 12345)).Code);

            AccountDetail seen = (AccountDetail)(await service.GetDetailAsync(ProjectAdminA, CustomerBoth)).Data!;
            MembershipView only = Assert.Single(seen.Memberships);   // chỉ thấy phần thuộc dự án A của mình
            Assert.Equal("A", only.ProjectCode);
        }

        // ---------- Khóa / mở ----------
        [Fact]
        public async Task Project_admin_locks_only_customers_fully_inside_own_projects()
        {
            var (service, store) = Build();

            Assert.True((await service.SetActiveAsync(ProjectAdminA, CustomerA, false)).Success);
            Assert.False(store.Accounts[CustomerA].Active);
            Assert.True((await service.SetActiveAsync(ProjectAdminA, CustomerA, true)).Success);

            Assert.Equal("FORBIDDEN", (await service.SetActiveAsync(ProjectAdminA, CustomerB, false)).Code);        // dự án khác
            Assert.Equal("FORBIDDEN", (await service.SetActiveAsync(ProjectAdminA, CustomerBoth, false)).Code);     // một phần ở dự án khác
            Assert.Equal("FORBIDDEN", (await service.SetActiveAsync(ProjectAdminA, Deployment, false)).Code);        // staff
            Assert.Equal("FORBIDDEN", (await service.SetActiveAsync(ProjectAdminA, Central, false)).Code);          // administrator trung tâm
            Assert.True(store.Accounts[Central].Active && store.Accounts[CustomerB].Active);
        }

        [Fact]
        public async Task Nobody_locks_themselves_and_other_central_administrators_can_be_locked_only_by_a_central_one()
        {
            var (service, store) = Build();

            Assert.Equal("SELF_LOCK", (await service.SetActiveAsync(Central, Central, false)).Code);
            Assert.True(store.Accounts[Central].Active);
            Assert.Contains(store.Audit, a => a.Outcome == "REJECTED" && a.Action == "account.lock" && a.Detail == "self");

            Assert.True((await service.SetActiveAsync(Central, SecondCentral, false)).Success);
            Assert.False(store.Accounts[SecondCentral].Active);
            // Tài khoản đã khóa không thao tác được nữa, nên administrator trung tâm cuối cùng không thể bị khóa bởi ai khác
            Assert.Equal("NOT_FOUND", (await service.SetActiveAsync(SecondCentral, Central, false)).Code);
            Assert.True(store.Accounts[Central].Active);
        }

        [Fact]
        public async Task Lock_events_are_audited_under_every_project_of_the_account_so_project_viewers_can_see_them()
        {
            var (service, store) = Build();

            await service.SetActiveAsync(Central, CustomerA, false);       // một dự án
            await service.SetActiveAsync(Central, CustomerBoth, false);    // hai dự án
            await service.SetActiveAsync(Central, SecondCentral, false);   // administrator trung tâm: không thuộc dự án

            Assert.Equal([1], store.Audit.Where(a => a.Action == "account.lock" && a.Target == "khach_a").Select(a => a.ProjectId!.Value));
            Assert.Equal([1, 2], store.Audit.Where(a => a.Action == "account.lock" && a.Target == "khach_ab").Select(a => a.ProjectId!.Value).Order());
            Assert.Null(Assert.Single(store.Audit, a => a.Action == "account.lock" && a.Target == "trungtam2").ProjectId);

            // Người chỉ có audit.read ở dự án A thấy sự kiện của khách A, không thấy sự kiện của dự án B
            store.Permissions["administrator"] = [.. store.Permissions["administrator"], PortalPermissions.AuditRead];
            AuditViewService viewer = new(store, new ProjectAccessService(store), TimeProvider.System);
            AuditView view = (AuditView)(await viewer.ListAsync(ProjectAdminA, new AuditFilterInput(null, null, null, "khach_a", null, null), 1)).Data!;
            Assert.Contains(view.Page.Items, e => e.Action == "account.lock" && e.ProjectId == 1);
            Assert.All(view.Page.Items, e => Assert.Equal(1, e.ProjectId));
        }

        [Fact]
        public async Task A_locked_account_loses_all_access_immediately()
        {
            var (service, store) = Build();
            ProjectAccessService access = new(store);
            Assert.NotNull(await access.GetAccessAsync(CustomerA));

            await service.SetActiveAsync(Central, CustomerA, false);

            Assert.Null(await access.GetAccessAsync(CustomerA));
        }

        // ---------- Membership ----------
        [Fact]
        public async Task Project_admin_manages_customer_memberships_only_inside_own_project()
        {
            var (service, store) = Build();

            Assert.True((await service.AddMembershipAsync(ProjectAdminA, Outsider, 1, "partner", "OUT_API")).Success);
            Assert.Equal("FORBIDDEN", (await service.AddMembershipAsync(ProjectAdminA, CustomerA, 2, "partner", null)).Code);          // dự án khác
            Assert.Equal("DUPLICATE", (await service.AddMembershipAsync(ProjectAdminA, Outsider, 1, "partner", null)).Code);
            Assert.Equal("INVALID_MEMBERSHIP", (await service.AddMembershipAsync(ProjectAdminA, Outsider, 1, "administrator", null)).Code);
            Assert.Equal("FORBIDDEN", (await service.AddMembershipAsync(ProjectAdminA, Deployment, 1, "administrator", null)).Code);   // staff chỉ trung tâm cấp
            Assert.Equal("INVALID_MEMBERSHIP", (await service.AddMembershipAsync(Central, CustomerA, null, "administrator", null)).Code);

            MembershipRow added = store.Memberships[Outsider].Single();
            Assert.True((await service.RemoveMembershipAsync(ProjectAdminA, added.Id)).Success);
            Assert.Empty(store.Memberships[Outsider]);

            MembershipRow elsewhere = store.Memberships[CustomerB].Single();
            Assert.Equal("FORBIDDEN", (await service.RemoveMembershipAsync(ProjectAdminA, elsewhere.Id)).Code);
            Assert.Single(store.Memberships[CustomerB]);
        }

        [Fact]
        public async Task Only_central_administrators_grant_staff_roles_and_the_last_one_cannot_be_removed()
        {
            var (service, store) = Build();

            Assert.True((await service.AddMembershipAsync(Central, Deployment, 2, "deployment", null)).Success);
            Assert.Equal("FORBIDDEN", (await service.AddMembershipAsync(ProjectAdminA, Deployment, 2, "deployment", null)).Code);

            Assert.True((await service.RemoveMembershipAsync(Central, store.Memberships[SecondCentral].Single().Id)).Success);
            MembershipRow lastAdmin = store.Memberships[Central].Single();
            Assert.Equal("LAST_ADMIN", (await service.RemoveMembershipAsync(Central, lastAdmin.Id)).Code);
            Assert.Single(store.Memberships[Central]);
        }

        // ---------- Đổi mật khẩu ----------
        [Fact]
        public async Task Changing_own_password_needs_the_current_one_and_a_strong_new_one()
        {
            var (service, store) = Build();

            Assert.Equal("INVALID_CREDENTIAL", (await service.ChangeOwnPasswordAsync(CustomerA, "sai-mat-khau", "MatKhauMoi-12345")).Code);
            Assert.Equal("INVALID_INPUT", (await service.ChangeOwnPasswordAsync(CustomerA, StrongPassword, "ngan")).Code);
            Assert.Equal("INVALID_INPUT", (await service.ChangeOwnPasswordAsync(CustomerA, StrongPassword, StrongPassword)).Code);

            Assert.True((await service.ChangeOwnPasswordAsync(CustomerA, StrongPassword, "MatKhauMoi-12345")).Success);
            Assert.True(PortalPasswordHasher.Verify("MatKhauMoi-12345", store.Accounts[CustomerA].PasswordHash));
            Assert.False(PortalPasswordHasher.Verify(StrongPassword, store.Accounts[CustomerA].PasswordHash));
            Assert.All(store.Audit, a => Assert.DoesNotContain("MatKhauMoi", (a.Target ?? "") + (a.Detail ?? "")));
        }

        // ---------- Dự án ----------
        [Fact]
        public async Task Only_central_administrator_manages_projects_and_urls_are_validated()
        {
            var (service, store) = Build();

            Assert.Equal("FORBIDDEN", (await service.ListProjectsAsync(ProjectAdminA)).Code);
            Assert.Equal("FORBIDDEN", (await service.CreateProjectAsync(ProjectAdminA, "CC", "Dự án C", "https://c.example")).Code);

            Assert.True((await service.CreateProjectAsync(Central, "CC", "Dự án C", "https://c.example/")).Success);
            Assert.Equal("https://c.example", store.AllProjects.Single(p => p.Code == "CC").ApiBaseUrl);
            Assert.Equal("DUPLICATE", (await service.CreateProjectAsync(Central, "cc", "Trùng", "https://c2.example")).Code);

            foreach (string bad in new[] { "ftp://c.example", "http://c.example", "https://user:pass@c.example", "https://c.example/?x=1", "https://c.example/#a", "javascript:alert(1)", "c.example", "", new string('a', 300) })
            {
                Assert.Equal("INVALID_INPUT", (await service.CreateProjectAsync(Central, "BADD", "Bad", bad)).Code);
            }
            Assert.Equal("INVALID_INPUT", (await service.CreateProjectAsync(Central, "co khoang", "Bad", "https://c.example")).Code);
            Assert.Equal("http://localhost:5000", AccountAdminService.NormalizeApiUrl("http://localhost:5000/"));

            Assert.True((await service.UpdateProjectAsync(Central, 1, "Dự án A mới", "https://a2.example", false)).Success);
            Assert.False(store.AllProjects.Single(p => p.Id == 1).Active);
            Assert.Equal("NOT_FOUND", (await service.UpdateProjectAsync(Central, 999, "X", "https://x.example", true)).Code);
        }

        [Fact]
        public async Task Every_denied_action_is_audited_with_the_actor()
        {
            var (service, store) = Build();

            await service.CreateAccountAsync(Deployment, "khach_x1", "X", "customer", StrongPassword, 1, "partner", null);
            await service.SetActiveAsync(ProjectAdminA, CustomerB, false);
            await service.AddMembershipAsync(ProjectAdminA, CustomerA, 2, "partner", null);
            await service.CreateProjectAsync(Deployment, "ZZ", "Z", "https://z.example");

            Assert.Equal(4, store.Audit.Count(a => a.Outcome == "DENIED"));
            Assert.All(store.Audit.Where(a => a.Outcome == "DENIED"), a => Assert.NotNull(a.ActorAccountId));
        }
    }
}
