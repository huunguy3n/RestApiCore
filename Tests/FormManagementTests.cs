using ApiDocs.ControlPlane;
using Business;
using Models;

namespace Tests
{
    /// <summary>
    /// Soạn form (revision, khóa lạc quan, publish, diff), gói DEV→PROD và nhật ký đồng bộ: quyền theo dự án và theo loại tài khoản,
    /// xung đột không ghi đè âm thầm, vòng đời đầy đủ từ lưu đến áp gói và nhập kết quả
    /// </summary>
    public sealed class FormManagementTests
    {
        private const int Author = 1, Publisher = 2, CentralAdmin = 3, CustomerA = 4, AuthorB = 5, Outsider = 6, SecondAuthor = 7;
        private const int A = 1, B = 2;
        private static readonly string Good = FormDefinitionValidator.Normalize(FormManagementService.NewFormTemplate);

        private static (FormManagementService Service, ControlPlaneFakeStore Store) Build()
        {
            ControlPlaneFakeStore store = new() { Projects = [new ProjectInfo(A, "A", "Dự án A"), new ProjectInfo(B, "B", "Dự án B")] };
            store.AllProjects.AddRange([new ProjectDetail(A, "A", "Dự án A", "https://a.example", true), new ProjectDetail(B, "B", "Dự án B", "https://b.example", true)]);
            store.Permissions["deployment"] = [PortalPermissions.FormsRead, PortalPermissions.FormsEdit];
            store.Permissions["administrator"] = [PortalPermissions.FormsRead, PortalPermissions.FormsEdit, PortalPermissions.FormsPublish, PortalPermissions.UsersRead];
            store.Permissions["partner"] = [PortalPermissions.FormsRead];

            void Account(int id, string login, string kind, params MembershipRow[] rows)
            {
                store.Accounts[id] = new PortalAccount(id, login, login, kind, true, "x");
                store.Memberships[id] = rows;
            }
            MembershipRow M(int id, int account, int? project, string role, string scope) => new(id, account, project, role, scope, null);

            Account(Author, "soan_a", AccountKind.Staff, M(1, Author, A, "deployment", "staff"));
            Account(SecondAuthor, "soan_a2", AccountKind.Staff, M(7, SecondAuthor, A, "deployment", "staff"));
            Account(Publisher, "duyet_a", AccountKind.Staff, M(2, Publisher, A, "administrator", "staff"));
            Account(CentralAdmin, "trungtam", AccountKind.Staff, M(3, CentralAdmin, null, "administrator", "staff"));
            Account(CustomerA, "khach_a", AccountKind.Customer, M(4, CustomerA, A, "partner", "customer"));
            Account(AuthorB, "soan_b", AccountKind.Staff, M(5, AuthorB, B, "deployment", "staff"));
            Account(Outsider, "khong_quyen", AccountKind.Staff);

            return (new FormManagementService(store, new ProjectAccessService(store), TimeProvider.System), store);
        }

        private static async Task<int> Save(FormManagementService service, int actor, int project, string form, int? baseRevision, string content)
        {
            OpResult result = await service.SaveAsync(actor, project, form, baseRevision, content);
            Assert.True(result.Success, result.Code);
            return ((SaveResult)result.Data!).Revision;
        }

        // ---------- Quyền ----------
        [Fact]
        public async Task Customers_and_users_without_access_never_see_form_contents_even_with_forms_read()
        {
            var (service, _) = Build();
            await Save(service, Author, A, "getCustomer", null, Good);

            foreach (int actor in new[] { CustomerA, Outsider, AuthorB, 999 })
            {
                Assert.Equal("FORBIDDEN", (await service.ListFormsAsync(actor, A)).Code);
                Assert.Equal("FORBIDDEN", (await service.GetFormAsync(actor, A, "getCustomer", null)).Code);
                Assert.Equal("FORBIDDEN", (await service.SaveAsync(actor, A, "getCustomer", 1, Good)).Code);
                Assert.Equal("FORBIDDEN", (await service.DiffAsync(actor, A, "getCustomer", 1, 1)).Code);
                Assert.Equal("FORBIDDEN", (await service.ListPackagesAsync(actor, A)).Code);
            }
            Assert.Equal("FORBIDDEN", (await service.ListSyncLogAsync(CustomerA, null, null, null, 1)).Code);
        }

        [Fact]
        public async Task Authors_edit_but_only_publishers_publish_and_package()
        {
            var (service, store) = Build();
            int revision = await Save(service, Author, A, "getCustomer", null, Good);

            Assert.Equal("FORBIDDEN", (await service.PublishAsync(Author, A, "getCustomer", revision)).Code);
            Assert.Equal("FORBIDDEN", (await service.CreatePackageAsync(Author, A)).Code);
            Assert.Equal("FORBIDDEN", (await service.SaveAsync(Publisher, B, "getCustomer", null, Good)).Code);   // quyền ở dự án A không sang B
            Assert.Contains(store.Audit, a => a.Action == "form.publish" && a.Outcome == "DENIED" && a.ActorAccountId == Author);

            Assert.True((await service.PublishAsync(Publisher, A, "getCustomer", revision)).Success);
            Assert.True((await service.PublishAsync(CentralAdmin, A, "getCustomer", await Save(service, CentralAdmin, A, "getCustomer", revision, Good + "\n"))).Success);   // administrator trung tâm làm được ở mọi dự án
        }

        // ---------- Lưu, trạng thái, xung đột ----------
        [Fact]
        public async Task Valid_saves_are_validated_broken_ones_stay_drafts_and_content_is_normalised()
        {
            var (service, store) = Build();

            OpResult ok = await service.SaveAsync(Author, A, "getCustomer", null, Good.Replace("\n", "\r\n"));
            OpResult broken = await service.SaveAsync(Author, A, "getCustomer", 1, Good.Replace("</import>", ""));

            Assert.Equal(("validated", 1), (((SaveResult)ok.Data!).Status, ((SaveResult)ok.Data!).Revision));
            SaveResult draft = (SaveResult)broken.Data!;
            Assert.Equal(("draft", 2), (draft.Status, draft.Revision));
            Assert.False(draft.Validation.IsValid);
            Assert.DoesNotContain('\r', store.Revisions.First(r => r.Revision == 1).Content);
            Assert.Equal(FormDefinitionValidator.Sha256(Good), store.Revisions.First(r => r.Revision == 1).Sha);
            Assert.All(store.Audit.Where(a => a.Action == "form.save"), a => Assert.DoesNotContain("SELECT", a.Detail ?? ""));   // audit không chứa nội dung form

            Assert.Equal("INVALID_INPUT", (await service.SaveAsync(Author, A, "setsale", null, Good)).Code);
            Assert.Equal("INVALID_INPUT", (await service.SaveAsync(Author, A, "getOther", null, new string('x', FormDefinitionValidator.MaxBytes + 1))).Code);
        }

        [Fact]
        public async Task Concurrent_edits_never_overwrite_each_other_silently()
        {
            var (service, store) = Build();
            await Save(service, Author, A, "getCustomer", null, Good);

            // Cả hai cùng mở revision 1; người thứ nhất lưu trước
            Assert.Equal(2, await Save(service, Author, A, "getCustomer", 1, Good.Replace("Mô tả form", "Bản của người một")));
            OpResult late = await service.SaveAsync(SecondAuthor, A, "getCustomer", 1, Good.Replace("Mô tả form", "Bản của người hai"));

            Assert.Equal("CONFLICT", late.Code);
            Assert.Equal(2, late.Data);
            Assert.Equal(2, store.Revisions.Count);
            Assert.Contains("Bản của người một", store.Revisions.First(r => r.Revision == 2).Content);

            // Form chưa có revision mà vẫn gửi baseRevision cũ cũng bị từ chối
            Assert.Equal("CONFLICT", (await service.SaveAsync(Author, A, "getItem", 3, Good)).Code);
            Assert.Equal(3, await Save(service, SecondAuthor, A, "getCustomer", 2, Good.Replace("Mô tả form", "Đã gộp")));
        }

        [Fact]
        public async Task Form_names_differing_only_by_case_cannot_coexist()
        {
            var (service, _) = Build();
            await Save(service, Author, A, "setSaleInvoice", null, Good);

            Assert.Equal("NAME_CASE_CONFLICT", (await service.SaveAsync(Author, A, "setSALEInvoice", null, Good)).Code);
        }

        // ---------- Publish ----------
        [Fact]
        public async Task Only_validated_revisions_publish_and_the_active_one_is_the_highest_published()
        {
            var (service, store) = Build();
            int draft = await Save(service, Author, A, "getCustomer", null, Good.Replace("</import>", ""));

            Assert.Equal("INVALID_STATE", (await service.PublishAsync(Publisher, A, "getCustomer", draft)).Code);
            Assert.Equal("NOT_FOUND", (await service.PublishAsync(Publisher, A, "getCustomer", 99)).Code);

            int v2 = await Save(service, Author, A, "getCustomer", draft, Good);
            Assert.True((await service.PublishAsync(Publisher, A, "getCustomer", v2)).Success);
            Assert.Equal("INVALID_STATE", (await service.PublishAsync(Publisher, A, "getCustomer", v2)).Code);   // đã publish rồi

            int v3 = await Save(service, Author, A, "getCustomer", v2, Good.Replace("Mô tả form", "Bản mới"));
            Assert.True((await service.PublishAsync(Publisher, A, "getCustomer", v3)).Success);
            Assert.Equal(v3, (await store.GetActiveFormsAsync(A)).Single().Revision);

            // Quay về bản cũ = lưu nội dung cũ thành revision mới rồi publish; lịch sử không bị xóa
            int v4 = await Save(service, Author, A, "getCustomer", v3, Good);
            Assert.True((await service.PublishAsync(Publisher, A, "getCustomer", v4)).Success);
            Assert.Equal(v4, (await store.GetActiveFormsAsync(A)).Single().Revision);
            Assert.Equal(4, (await store.GetRevisionsAsync(A, "getCustomer")).Count);
        }

        // ---------- Diff ----------
        [Fact]
        public async Task Diff_shows_added_and_removed_lines_and_refuses_huge_inputs()
        {
            var (service, store) = Build();
            await Save(service, Author, A, "getCustomer", null, Good);
            await Save(service, Author, A, "getCustomer", 1, Good.Replace("Mô tả form", "Mô tả mới"));

            IReadOnlyList<DiffLine> diff = (IReadOnlyList<DiffLine>)(await service.DiffAsync(Author, A, "getCustomer", 1, 2)).Data!;

            Assert.Single(diff, l => l.Kind == '-');
            Assert.Single(diff, l => l.Kind == '+');
            Assert.Contains("Mô tả mới", diff.Single(l => l.Kind == '+').Text);
            Assert.Contains("Mô tả form", diff.Single(l => l.Kind == '-').Text);
            Assert.Equal(Good.Split('\n').Length, diff.Count(l => l.Kind != '+'));

            Assert.DoesNotContain(FormManagementService.Diff(["a", "b"], ["a", "b"]), l => l.Kind != ' ');
            Assert.Equal("NOT_FOUND", (await service.DiffAsync(Author, A, "getCustomer", 1, 9)).Code);
            string huge = string.Join("\n", Enumerable.Range(0, FormManagementService.MaxDiffLines + 5).Select(i => "dong " + i));
            store.Revisions[0] = store.Revisions[0] with { Content = huge };
            Assert.Equal("INVALID_INPUT", (await service.DiffAsync(Author, A, "getCustomer", 1, 2)).Code);
        }

        // ---------- Vòng đời đầy đủ: gói và đồng bộ ----------
        private static async Task<(int PackageId, byte[] Zip, string Sha)> PublishedPackage(FormManagementService service, ControlPlaneFakeStore store)
        {
            await service.PublishAsync(Publisher, A, "getCustomer", await Save(service, Author, A, "getCustomer", null, Good));
            await service.PublishAsync(Publisher, A, "getItem", await Save(service, Author, A, "getItem", null, Good));
            OpResult created = await service.CreatePackageAsync(Publisher, A);
            Assert.True(created.Success, created.Code);
            int id = (int)created.Data!;
            PackageFile file = (PackageFile)(await service.DownloadPackageAsync(Publisher, A, id)).Data!;
            return (id, file.Bytes, store.Packages.Single(p => p.Id == id).Sha);
        }

        [Fact]
        public async Task Package_contains_the_active_forms_downloads_intact_and_logs_the_export()
        {
            var (service, store) = Build();
            Assert.Equal("NO_FORMS", (await service.CreatePackageAsync(Publisher, A)).Code);

            var (id, zip, sha) = await PublishedPackage(service, store);
            PackageContents contents = FormPackage.Read(zip);

            Assert.Equal(sha, contents.ManifestSha256);
            Assert.Equal(["getCustomer", "getItem"], contents.Manifest.Forms.Select(f => f.Name));
            Assert.Equal("A", contents.Manifest.Project);
            Assert.Equal(id, contents.Manifest.PackageId);
            SyncLogEntry export = Assert.Single(store.SyncLog);
            Assert.Equal(("EXPORT", "DEV", "portal", "OK"), (export.Event, export.Env, export.Source, export.Outcome));
            Assert.Equal("FORBIDDEN", (await service.DownloadPackageAsync(Author, A, id)).Code);   // tác giả không tải được gói
        }

        [Fact]
        public async Task A_package_whose_stored_content_changed_is_not_delivered()
        {
            var (service, store) = Build();
            var (id, _, _) = await PublishedPackage(service, store);

            int index = store.Revisions.FindIndex(r => r.Form == "getItem");
            store.Revisions[index] = store.Revisions[index] with { Content = store.Revisions[index].Content + "<!-- sửa lén -->" };

            Assert.Equal("INTEGRITY", (await service.DownloadPackageAsync(Publisher, A, id)).Code);
            Assert.Contains(store.Audit, a => a.Action == "package.download" && a.Outcome == "REJECTED");
        }

        [Fact]
        public async Task Applying_the_package_with_the_cli_and_importing_the_result_completes_the_sync_log()
        {
            var (service, store) = Build();
            var (id, zip, sha) = await PublishedPackage(service, store);
            string target = Path.Combine(Path.GetTempPath(), "formpkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Chạy thử trên PROD rồi áp thật; mỗi lần xuất một file kết quả. Thư mục đích phải có sẵn
                Directory.CreateDirectory(target);
                string verifyJson = FormPackageApplier.ResultToJson(FormPackageApplier.Run(zip, sha, target, apply: false, "PROD", "nguyen\u0007van", TimeProvider.System));
                string applyJson = FormPackageApplier.ResultToJson(FormPackageApplier.Run(zip, sha, target, apply: true, "PROD", "nguyen van", TimeProvider.System));
                Assert.True(File.Exists(Path.Combine(target, "getCustomer.xml")));

                Assert.True((await service.RecordApplyResultAsync(Publisher, A, verifyJson)).Success);
                Assert.True((await service.RecordApplyResultAsync(Publisher, A, applyJson)).Success);

                SyncLogEntry[] log = store.SyncLog.ToArray();
                Assert.Equal(["EXPORT", "VERIFY", "APPLY"], log.Select(l => l.Event));
                Assert.All(log.Skip(1), l => Assert.Equal(("PROD", "cli", "OK", id), (l.Env, l.Source, l.Outcome, l.PackageId)));
                Assert.DoesNotContain('\u0007', log[1].Actor!);   // ký tự điều khiển trong tên người thao tác bị loại
                Assert.Contains("nhập bởi duyet_a", log[2].Detail);
                Assert.Contains("NEW 2", log[2].Detail);

                SyncLogPage page = (SyncLogPage)(await service.ListSyncLogAsync(Publisher, A, "PROD", "OK", 1)).Data!;
                Assert.Equal(2, page.Total);
            }
            finally
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
            }
        }

        [Fact]
        public async Task Forged_foreign_or_malformed_results_are_not_accepted()
        {
            var (service, store) = Build();
            var (id, zip, sha) = await PublishedPackage(service, store);
            string good = FormPackageApplier.ResultToJson(FormPackageApplier.Run(zip, sha, Path.Combine(Path.GetTempPath(), "formpkg-" + Guid.NewGuid().ToString("N")), apply: false, "PROD", "nguyen", TimeProvider.System));
            int before = store.SyncLog.Count;

            Assert.Equal("PACKAGE_MISMATCH", (await service.RecordApplyResultAsync(Publisher, A, good.Replace(sha, new string('a', 64)))).Code);          // sai SHA-256 manifest
            Assert.Equal("PACKAGE_MISMATCH", (await service.RecordApplyResultAsync(Publisher, A, good.Replace("\"packageId\": " + id, "\"packageId\": 99"))).Code);   // gói không tồn tại
            Assert.Equal("PACKAGE_MISMATCH", (await service.RecordApplyResultAsync(Publisher, A, good.Replace("getItem", "setKhacHan"))).Code);          // form không thuộc gói
            Assert.Equal("INVALID_INPUT", (await service.RecordApplyResultAsync(Publisher, A, good.Replace("\"PROD\"", "\"STAGING\""))).Code);
            Assert.Equal("INVALID_INPUT", (await service.RecordApplyResultAsync(Publisher, A, good.Replace("\"NEW\"", "\"HACKED\""))).Code);
            Assert.Equal("INVALID_INPUT", (await service.RecordApplyResultAsync(Publisher, A, "{ khong phai json")).Code);
            Assert.Equal("INVALID_INPUT", (await service.RecordApplyResultAsync(Publisher, A, new string('x', FormManagementService.MaxResultBytes + 1))).Code);
            Assert.Equal("FORBIDDEN", (await service.RecordApplyResultAsync(Author, A, good)).Code);                 // tác giả không ghi nhận được
            Assert.Equal("FORBIDDEN", (await service.RecordApplyResultAsync(AuthorB, B, good)).Code);                // dự án khác
            Assert.Equal(before, store.SyncLog.Count);
        }

        [Fact]
        public async Task Sync_log_listing_is_limited_to_projects_the_viewer_can_read()
        {
            var (service, store) = Build();
            await store.AddSyncLogAsync(new SyncLogRecord(A, "PROD", "APPLY", 1, "cli", "x", "OK", null));
            await store.AddSyncLogAsync(new SyncLogRecord(B, "PROD", "APPLY", 2, "cli", "y", "FAILED", null));

            Assert.Equal(1, ((SyncLogPage)(await service.ListSyncLogAsync(Author, null, null, null, 1)).Data!).Total);
            Assert.Equal(2, ((SyncLogPage)(await service.ListSyncLogAsync(CentralAdmin, null, null, null, 1)).Data!).Total);
            Assert.Equal("FORBIDDEN", (await service.ListSyncLogAsync(Author, B, null, null, 1)).Code);
            Assert.Equal("INVALID_INPUT", (await service.ListSyncLogAsync(CentralAdmin, null, "STAGING", null, 1)).Code);
        }
    }
}
