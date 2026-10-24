using System.Data;
using WebPortal.ControlPlane;
using Microsoft.Data.SqlClient;
using Models;
using Provider;

namespace Tests
{
    /// <summary>Chỉ chạy khi có DB portal thật (đã chạy sql/portal/001-004).</summary>
    public sealed class SqlServerFactAttribute : FactAttribute
    {
        public const string Variable = "FAST_PORTAL_SQL";
        public const string DisposableVariable = "FAST_PORTAL_SQL_DISPOSABLE";

        public SqlServerFactAttribute()
        {
            string? connection = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(connection))
            {
                Skip = $"Đặt {Variable}=<connection string tới DB 'FastPortalTest*' đã chạy sql/portal/001-004> để chạy test trên SQL Server thật";
            }
            else if (!new SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("FastPortalTest", StringComparison.OrdinalIgnoreCase)
                && Environment.GetEnvironmentVariable(DisposableVariable) != "1")
            {
                // Các test này ghi và TẮT mọi administrator trung tâm có sẵn: chỉ chạy trên DB dùng một lần.
                // DB tên khác phải được chủ động khai là dùng một lần bằng FAST_PORTAL_SQL_DISPOSABLE=1
                Skip = $"Tên database phải bắt đầu bằng FastPortalTest, hoặc đặt {DisposableVariable}=1 nếu chắc đây là DB dùng một lần (test ghi / khóa dữ liệu)";
            }
        }
    }

    /// <summary>
    /// Kiểm SqlControlPlaneStore trên SQL Server thật: truy vấn, ràng buộc, transaction và thao tác đồng thời
    /// (revision form, khóa nguyên tử administrator trung tâm cuối cùng)
    /// </summary>
    [Collection("SqlServerPortal")]
    public sealed class SqlServerStoreTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        private static int Deadlocks;
        private static readonly string Connection = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable) ?? "";

        private static SqlDatabase Database() => new(Connection, 30);

        private static SqlControlPlaneStore Store() => new(Database());

        private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

        private static async Task<int> Count(string sql, params SqlParameter[] parameters) =>
            Convert.ToInt32((await Database().RetrieveAsync(sql, default, parameters)).Tables[0].Rows[0][0]);

        private static async Task<int> NewProject(SqlControlPlaneStore store, string code) =>
            (await store.CreateProjectAsync(code, "Dự án " + code, "https://" + code.ToLowerInvariant() + ".example"))!.Value;

        private static async Task<int> NewAccount(SqlControlPlaneStore store, string login, string kind, int? project, string role, string? apiUser = null) =>
            (await store.CreateAccountAsync(login, "Tên " + login, kind, "v1.20000.QUJDREVGR0hJSktMTU5PUA==.QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo=", new NewMembership(project, role, apiUser), null))
                ?? throw new InvalidOperationException("không tạo được " + login);

        // ---------- Seed và dự án ----------
        [SqlServerFact]
        public async Task Seed_roles_have_only_known_permissions_and_partner_cannot_do_more_than_read_forms()
        {
            SqlControlPlaneStore store = Store();

            IReadOnlyList<string> administrator = await store.GetRolePermissionsAsync("administrator");
            IReadOnlyList<string> deployment = await store.GetRolePermissionsAsync("deployment");

            Assert.Equal([PortalPermissions.FormsRead], await store.GetRolePermissionsAsync("partner"));
            Assert.Contains(PortalPermissions.UsersCreate, administrator);
            Assert.DoesNotContain(PortalPermissions.UsersRead, deployment);
            Assert.DoesNotContain(PortalPermissions.LogsBodyRead, administrator);   // quyền nhạy cảm không gán mặc định
            Assert.All(administrator.Concat(deployment), permission => Assert.Contains(permission, PortalPermissions.All));
            Assert.Empty(await store.GetRolePermissionsAsync("khong-co-vai-tro"));
        }

        [SqlServerFact]
        public async Task Projects_are_unique_case_insensitively_and_inactive_ones_leave_the_active_list()
        {
            SqlControlPlaneStore store = Store();
            string code = "PRJ" + Suffix();

            int id = await NewProject(store, code);
            Assert.Null(await store.CreateProjectAsync(code.ToLowerInvariant(), "Trùng", "https://x.example"));

            Assert.Contains(await store.GetActiveProjectsAsync(), p => p.Id == id);
            Assert.True(await store.UpdateProjectAsync(id, "Đã đổi tên", "https://moi.example", active: false));
            Assert.DoesNotContain(await store.GetActiveProjectsAsync(), p => p.Id == id);
            ProjectDetail stored = (await store.GetAllProjectsAsync()).Single(p => p.Id == id);
            Assert.Equal(("Đã đổi tên", "https://moi.example", false), (stored.Name, stored.ApiBaseUrl, stored.Active));
            Assert.False(await store.UpdateProjectAsync(-5, "x", "https://x.example", true));
        }

        // ---------- Tài khoản và ràng buộc ----------
        [SqlServerFact]
        public async Task Account_creation_is_all_or_nothing_and_logins_and_api_users_are_unique()
        {
            SqlControlPlaneStore store = Store();
            string sfx = Suffix();
            int project = await NewProject(store, "ACC" + sfx);
            int first = await NewAccount(store, "khach_" + sfx, "customer", project, "partner", "API_" + sfx);

            PortalAccount found = (await store.FindAccountByLoginAsync("KHACH_" + sfx.ToUpperInvariant()))!;   // không phân biệt hoa thường
            Assert.Equal(first, found.Id);
            Assert.True(found.Active);
            MembershipRow membership = Assert.Single(await store.GetMembershipsAsync(first));
            Assert.Equal((project, "partner", "customer", "API_" + sfx), (membership.ProjectId, membership.RoleCode, membership.RoleScope, membership.ProjectUserName));

            // Trùng tên đăng nhập
            Assert.Null(await store.CreateAccountAsync("khach_" + sfx, "Trùng", "customer", "x", new NewMembership(project, "partner", null), null));
            // Vai trò không tồn tại: giao dịch hoàn tác, không để lại tài khoản mồ côi
            Assert.Null(await store.CreateAccountAsync("mo_coi_" + sfx, "Mồ côi", "customer", "x", new NewMembership(project, "khong-co", null), null));
            Assert.Null(await store.FindAccountByLoginAsync("mo_coi_" + sfx));
            // Tài khoản API đã thuộc khách khác trong cùng dự án (chỉ mục lọc sql/portal/004)
            Assert.Null(await store.CreateAccountAsync("khach2_" + sfx, "Khách hai", "customer", "x", new NewMembership(project, "partner", "api_" + sfx), null));
            Assert.Null(await store.FindAccountByLoginAsync("khach2_" + sfx));

            // Cùng tên API ở dự án khác thì được; null không bị coi là trùng
            int other = await NewProject(store, "ACD" + sfx);
            int second = await NewAccount(store, "khach3_" + sfx, "customer", other, "partner", "API_" + sfx);
            int third = await NewAccount(store, "khach4_" + sfx, "customer", other, "partner");
            Assert.True(await store.AddMembershipAsync(third, new NewMembership(project, "partner", null), null));
            Assert.False(await store.AddMembershipAsync(third, new NewMembership(project, "partner", null), null));   // trùng (tài khoản, vai trò, dự án)
            Assert.False(await store.AddMembershipAsync(second, new NewMembership(project, "khong-co", null), null));
            Assert.Equal(2, (await store.GetMembershipsAsync(third)).Count);
        }

        [SqlServerFact]
        public async Task Listing_escapes_like_characters_pages_and_respects_project_scope()
        {
            SqlControlPlaneStore store = Store();
            string sfx = Suffix();
            int project = await NewProject(store, "LST" + sfx), other = await NewProject(store, "LSU" + sfx);
            int a = await NewAccount(store, $"ab_c{sfx}", "customer", project, "partner");
            await NewAccount(store, $"abXc{sfx}", "customer", project, "partner");
            await NewAccount(store, $"zz{sfx}", "customer", other, "partner");

            Assert.Equal(a, Assert.Single((await store.ListAccountsAsync($"ab_c{sfx}", 1, 10, null)).Items).Id);   // "_" là ký tự thường, không khớp "abXc"
            Assert.Equal(0, (await store.ListAccountsAsync("%", 1, 10, null)).Total);
            Assert.Equal(0, (await store.ListAccountsAsync("[", 1, 10, null)).Total);
            Assert.Equal(0, (await store.ListAccountsAsync($"a'; DROP TABLE portalAccount;--", 1, 10, null)).Total);

            AccountPage scoped = await store.ListAccountsAsync(sfx, 1, 10, [project]);
            Assert.Equal(2, scoped.Total);
            Assert.DoesNotContain(scoped.Items, i => i.Login == $"zz{sfx}");
            Assert.Equal(0, (await store.ListAccountsAsync(sfx, 1, 10, [])).Total);

            AccountPage page1 = await store.ListAccountsAsync(sfx, 1, 2, null), page2 = await store.ListAccountsAsync(sfx, 2, 2, null);
            Assert.Equal((3, 2, 1), (page1.Total, page1.Items.Count, page2.Items.Count));
            Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id)));
        }

        // ---------- Khóa nguyên tử administrator trung tâm ----------
        /// <summary>Test dùng DB dùng một lần: tắt mọi administrator trung tâm có sẵn để đếm được chính xác</summary>
        private static async Task<(int First, int Second)> TwoCentralAdministrators(SqlControlPlaneStore store)
        {
            await Database().ExecuteAsync("""
                UPDATE a SET status = '0' FROM portalAccount a WHERE a.kind = 'staff' AND EXISTS (
                    SELECT 1 FROM portalMembership m JOIN portalRole r ON r.id = m.role_id WHERE m.account_id = a.id AND m.project_id IS NULL AND r.code = 'administrator')
                """);
            string sfx = Suffix();
            return (await NewAccount(store, "tt1_" + sfx, "staff", null, "administrator"), await NewAccount(store, "tt2_" + sfx, "staff", null, "administrator"));
        }

        private static Task<int> ActiveCentralAdministrators() => Count("""
            SELECT COUNT(DISTINCT a.id) FROM portalAccount a JOIN portalMembership m ON m.account_id = a.id AND m.project_id IS NULL
            JOIN portalRole r ON r.id = m.role_id AND r.code = 'administrator' WHERE a.kind = 'staff' AND a.status = '1'
            """);

        [SqlServerFact]
        public async Task The_last_central_administrator_cannot_be_locked_or_unassigned_even_when_two_people_try_at_the_same_time()
        {
            SqlControlPlaneStore store = Store();
            (int one, int two) = await TwoCentralAdministrators(store);
            Assert.Equal(2, await ActiveCentralAdministrators());

            // Tuần tự: còn hai thì khóa được một, người cuối cùng thì bị chặn (có bảo vệ), không bảo vệ thì khóa được
            Assert.True(await store.SetAccountActiveAsync(one, false, keepOneCentralAdministrator: true));
            Assert.False(await store.SetAccountActiveAsync(two, false, keepOneCentralAdministrator: true));
            Assert.Equal(1, await ActiveCentralAdministrators());
            Assert.True(await store.SetAccountActiveAsync(one, true, keepOneCentralAdministrator: false));

            // Đồng thời: nhiều vòng, mỗi vòng hai người cùng khóa nhau; đúng một người thành công, luôn còn ít nhất một administrator
            int deadlockVictims = 0, deadlocksSeen = 0;
            for (int round = 0; round < 25; round++)
            {
                bool[] outcome = await Task.WhenAll(Attempt(() => store.SetAccountActiveAsync(one, false, true)), Attempt(() => store.SetAccountActiveAsync(two, false, true)));
                int succeeded = outcome.Count(o => o);
                Assert.True(succeeded <= 1, $"vòng {round}: cả hai cùng khóa được");
                Assert.True(await ActiveCentralAdministrators() >= 1, $"vòng {round}: không còn administrator");
                if (succeeded == 0)
                {
                    deadlockVictims++;
                }
                await store.SetAccountActiveAsync(one, true, false);
                await store.SetAccountActiveAsync(two, true, false);
            }
            output.WriteLine($"khóa nguyên tử: 25 vòng, {deadlockVictims} vòng không ai thành công, {Volatile.Read(ref Deadlocks)} lần deadlock");

            static async Task<bool> Attempt(Func<Task<bool>> action)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        return await action();
                    }
                    catch (SqlException ex) when (ex.Number == 1205)
                    {
                        // Nạn nhân deadlock: thử lại, kết quả an toàn dù thế nào
                        Interlocked.Increment(ref Deadlocks);
                    }
                }
                return false;
            }
        }

        [SqlServerFact]
        public async Task Concurrent_removal_of_the_two_central_administrator_memberships_leaves_exactly_one()
        {
            SqlControlPlaneStore store = Store();
            (int one, int two) = await TwoCentralAdministrators(store);
            MembershipRow firstMembership = (await store.GetMembershipsAsync(one)).Single(), secondMembership = (await store.GetMembershipsAsync(two)).Single();

            for (int round = 0; round < 15; round++)
            {
                bool[] outcome = await Task.WhenAll(Retry(() => store.RemoveMembershipAsync(firstMembership.Id, true)), Retry(() => store.RemoveMembershipAsync(secondMembership.Id, true)));

                Assert.True(outcome.Count(o => o) <= 1, $"vòng {round}: gỡ được cả hai");
                Assert.True(await ActiveCentralAdministrators() >= 1, $"vòng {round}: không còn administrator");

                // Dựng lại hai membership cho vòng sau
                await store.AddMembershipAsync(one, new NewMembership(null, "administrator", null), null);
                await store.AddMembershipAsync(two, new NewMembership(null, "administrator", null), null);
                firstMembership = (await store.GetMembershipsAsync(one)).Single();
                secondMembership = (await store.GetMembershipsAsync(two)).Single();
            }

            static async Task<bool> Retry(Func<Task<bool>> action)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        return await action();
                    }
                    catch (SqlException ex) when (ex.Number == 1205)
                    {
                    }
                }
                return false;
            }
        }

        // ---------- Form: revision, gói, đồng bộ ----------
        [SqlServerFact]
        public async Task Concurrent_saves_from_the_same_base_revision_produce_exactly_one_winner()
        {
            SqlControlPlaneStore store = Store();
            string sfx = Suffix();
            int project = await NewProject(store, "REV" + sfx);
            int author = await NewAccount(store, "tg_" + sfx, "staff", project, "deployment");
            string form = "getRev" + sfx[..4];

            // 10 người cùng lưu revision đầu tiên (base null)
            int?[] results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        return await store.AddRevisionAsync(project, form, null, $"noi dung {i}", new string('a', 64), "validated", author);
                    }
                    catch (SqlException ex) when (ex.Number == 1205)
                    {
                    }
                }
                return null;
            }));

            Assert.Single(results, r => r != null);
            RevisionInfo only = Assert.Single(await store.GetRevisionsAsync(project, form));
            Assert.Equal(1, only.Revision);

            // Chuỗi tuần tự đúng thì tăng dần; base cũ bị từ chối
            Assert.Equal(2, await store.AddRevisionAsync(project, form, 1, "hai", new string('b', 64), "draft", author));
            Assert.Null(await store.AddRevisionAsync(project, form, 1, "lệch", new string('c', 64), "draft", author));
            Assert.Equal(3, await store.AddRevisionAsync(project, form, 2, "ba", new string('d', 64), "validated", author));
            Assert.Equal([3, 2, 1], (await store.GetRevisionsAsync(project, form)).Select(r => r.Revision));
            Assert.Equal("ba", (await store.GetRevisionAsync(project, form, 3))!.Content);
            Assert.Equal("tg_" + sfx, (await store.GetRevisionAsync(project, form, 3))!.Info.CreatedBy);
        }

        [SqlServerFact]
        public async Task Publishing_packaging_and_the_sync_log_work_end_to_end_with_rollback_on_failure()
        {
            SqlControlPlaneStore store = Store();
            string sfx = Suffix();
            int project = await NewProject(store, "PKG" + sfx);
            int author = await NewAccount(store, "tp_" + sfx, "staff", project, "administrator");
            string a = "getAaa" + sfx[..4], b = "getBbb" + sfx[..4];

            await store.AddRevisionAsync(project, a, null, "a1", new string('1', 64), "validated", author);
            await store.AddRevisionAsync(project, a, 1, "a2", new string('2', 64), "validated", author);
            await store.AddRevisionAsync(project, b, null, "b1", new string('3', 64), "draft", author);

            Assert.False(await store.PublishRevisionAsync(project, b, 1, author));   // chỉ validated mới publish được
            Assert.True(await store.PublishRevisionAsync(project, a, 1, author));
            Assert.False(await store.PublishRevisionAsync(project, a, 1, author));   // đã publish rồi
            Assert.True(await store.PublishRevisionAsync(project, a, 2, author));

            ActiveForm active = Assert.Single(await store.GetActiveFormsAsync(project));
            Assert.Equal((a, 2), (active.Name, active.Revision));   // revision publish lớn nhất
            FormSummary summary = (await store.ListFormsAsync(project)).Single(f => f.Name == a);
            Assert.Equal((2, "published", 2), (summary.LatestRevision, summary.LatestStatus, summary.ActiveRevision));
            Assert.Null((await store.ListFormsAsync(project)).Single(f => f.Name == b).ActiveRevision);

            // Dựng gói: build chạy trong transaction, nhận id gói
            int packageId = await store.CreatePackageAsync(project, author, id => new PackageDraft($"{{\"id\":{id}}}", new string('f', 64), [active.RevisionId]));
            PackageRecord record = (await store.GetPackageAsync(project, packageId))!;
            Assert.Equal($"{{\"id\":{packageId}}}", record.ManifestJson);
            Assert.Equal((a, 2, "a2"), (record.Items[0].Form, record.Items[0].Revision, record.Items[0].Content));
            Assert.Equal(1, record.Info.FormCount);
            Assert.Null(await store.GetPackageAsync(project + 1000, packageId));   // gói thuộc dự án khác thì không thấy

            // build ném lỗi: không để lại dòng gói nào
            int before = (await store.ListPackagesAsync(project)).Count;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreatePackageAsync(project, author, _ => throw new InvalidOperationException("hỏng")));
            Assert.Equal(before, (await store.ListPackagesAsync(project)).Count);
            Assert.Equal(0, await Count("SELECT COUNT(1) FROM portalPackage WHERE project_id = @p AND manifest = N'{}'", new SqlParameter("@p", project)));

            // Nhật ký đồng bộ: lọc, phân trang, làm sạch ký tự điều khiển, giới hạn theo dự án
            await store.AddSyncLogAsync(new SyncLogRecord(project, "DEV", "EXPORT", packageId, "portal", "tp", "OK", "ok"));
            await store.AddSyncLogAsync(new SyncLogRecord(project, "PROD", "APPLY", packageId, "cli", "nguyen\nvan", "FAILED", "loi\r\n[GIA] da duyet"));
            SyncLogPage all = await store.ListSyncLogAsync([project], null, "", "", 1, 10);
            Assert.Equal(2, all.Total);
            Assert.Equal("APPLY", all.Items[0].Event);   // mới nhất trước
            Assert.Equal("nguyenvan", all.Items[0].Actor);
            Assert.DoesNotContain('\n', all.Items[0].Detail!);
            Assert.Equal(1, (await store.ListSyncLogAsync([project], null, "PROD", "FAILED", 1, 10)).Total);
            Assert.Equal(0, (await store.ListSyncLogAsync([project + 1000], null, "", "", 1, 10)).Total);
            Assert.Equal(0, (await store.ListSyncLogAsync([], null, "", "", 1, 10)).Total);
            Assert.Equal(1, (await store.ListSyncLogAsync([project], null, "", "", 2, 1)).Items.Count);
        }

        // ---------- Audit, đếm, thống kê ----------
        [SqlServerFact]
        public async Task Audit_filters_scope_control_characters_and_dashboard_counts_match_the_data()
        {
            SqlControlPlaneStore store = Store();
            string sfx = Suffix();
            int project = await NewProject(store, "AUD" + sfx), other = await NewProject(store, "AUE" + sfx);
            int actor = await NewAccount(store, "ad_" + sfx, "staff", project, "administrator");
            int customer = await NewAccount(store, "kh_" + sfx, "customer", project, "partner");
            DateTime start = DateTime.Now.AddSeconds(-1);   // chỉ tính dòng ghi trong test này (DB dùng một lần, lớp này là nơi duy nhất ghi audit)

            await store.WriteAuditAsync(new AuditRecord(actor, project, "account.create", "kh_" + sfx, "SUCCESS", "bình thường"));
            await store.WriteAuditAsync(new AuditRecord(actor, other, "account.lock", "x\ny" + sfx, "DENIED", "dòng 1\r\ndòng 2 [GIẢ MẠO]"));
            await store.WriteAuditAsync(new AuditRecord(null, null, "login", "ke_la" + sfx, "DENIED"));

            AuditPage everything = await store.ListAuditAsync(new AuditQuery(start, start.AddHours(1), null, sfx, "", ""), 1, 20, null);
            Assert.Equal(3, everything.Total);   // khớp theo người HOẶC đối tượng
            Assert.DoesNotContain(everything.Items, e => (e.Target + e.Detail).Contains('\n') || (e.Target + e.Detail).Contains('\r'));
            Assert.Equal("ad_" + sfx, everything.Items.First(e => e.Action == "account.create").ActorLogin);
            Assert.Equal("AUD" + sfx, everything.Items.First(e => e.Action == "account.create").ProjectCode);

            Assert.Equal(1, (await store.ListAuditAsync(new AuditQuery(start, start.AddHours(1), project, sfx, "", ""), 1, 20, null)).Total);
            Assert.Equal(1, (await store.ListAuditAsync(new AuditQuery(start, start.AddHours(1), null, sfx, "account.l", ""), 1, 20, null)).Total);
            Assert.Equal(2, (await store.ListAuditAsync(new AuditQuery(start, start.AddHours(1), null, sfx, "", "DENIED"), 1, 20, null)).Total);
            Assert.Equal(0, (await store.ListAuditAsync(new AuditQuery(start, start.AddHours(1), null, "%", "", ""), 1, 20, null)).Total);   // % là ký tự thường
            Assert.Equal(1, (await store.ListAuditAsync(new AuditQuery(start, start.AddHours(1), null, sfx, "", ""), 1, 20, [project])).Total);   // phạm vi dự án: không thấy dòng của dự án khác / không dự án
            Assert.Equal(0, (await store.ListAuditAsync(new AuditQuery(start, start.AddHours(1), null, sfx, "", ""), 1, 20, [])).Total);
            Assert.Equal(0, (await store.ListAuditAsync(new AuditQuery(start.AddDays(-9), start.AddDays(-8), null, sfx, "", ""), 1, 20, null)).Total);

            Assert.Equal(2, await store.CountAuditAsync(start, "DENIED", null, null));              // thao tác bị từ chối + đăng nhập sai (không thuộc dự án)
            Assert.Equal(1, await store.CountAuditAsync(start, "DENIED", "login", null));
            Assert.Equal(0, await store.CountAuditAsync(start, "DENIED", null, [project]));        // dòng DENIED nằm ở dự án khác / không dự án
            Assert.Equal(1, await store.CountAuditAsync(start, "DENIED", null, [other]));
            Assert.Equal(0, await store.CountAuditAsync(start, "DENIED", null, []));

            ProjectStat stat = (await store.GetProjectStatsAsync([project], start)).Single();
            Assert.Equal(("AUD" + sfx, true, 1, 1, 0), (stat.Code, stat.Active, stat.ActiveCustomers, stat.ActiveStaff, stat.Denied7Days));
            Assert.Equal(1, (await store.GetProjectStatsAsync([other], start)).Single().Denied7Days);
            Assert.Empty(await store.GetProjectStatsAsync([], start));

            AccountCounts scoped = await store.GetAccountCountsAsync([project]);
            Assert.Equal((1, 0, 1, 0), (scoped.StaffActive, scoped.StaffLocked, scoped.CustomerActive, scoped.CustomerLocked));
            await store.SetAccountActiveAsync(customer, false, keepOneCentralAdministrator: false);
            AccountCounts afterLock = await store.GetAccountCountsAsync([project]);
            Assert.Equal((0, 1), (afterLock.CustomerActive, afterLock.CustomerLocked));
            Assert.Equal((0, 0, 0, 0), (await store.GetAccountCountsAsync([])).ToTuple());
        }
    }

    internal static class AccountCountsExtensions
    {
        public static (int, int, int, int) ToTuple(this AccountCounts c) => (c.StaffActive, c.StaffLocked, c.CustomerActive, c.CustomerLocked);
    }
}
