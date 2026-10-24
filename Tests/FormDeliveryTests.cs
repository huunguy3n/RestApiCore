using System.IO.Compression;
using System.Text;
using WebPortal.Portal;
using Business;

namespace Tests
{
    /// <summary>Kiểm form (XML an toàn, luật loader, SQL bị chặn), gói zip (toàn vẹn, tên mục, giới hạn) và áp gói lên thư mục đích (dry-run, nguyên tử, sao lưu)</summary>
    public sealed class FormDeliveryTests
    {
        private static readonly string Valid = FormDefinitionValidator.Normalize(FormManagementService.NewFormTemplate);

        private static string WithSql(string sql) => Valid.Replace("SELECT RTRIM(a.ma_kh) AS customerId, a.ten_kh AS customerName FROM dmkh a WHERE a.status = '1'", sql);

        // ---------- Kiểm form ----------
        [Fact]
        public void Template_and_every_sample_form_in_the_repo_pass_validation()
        {
            Assert.True(FormDefinitionValidator.Validate("getCustomer", Valid).IsValid);

            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "WebApi", "App_Data", "Form")))
            {
                dir = dir.Parent;
            }
            // setTemplate.xml là mẫu cho người soạn (không phải form chạy được), nên chỉ kiểm các form v3 thật
            string[] samples = new[] { "getCustomer", "setSaleInvoice" }.Select(n => Path.Combine(dir!.FullName, "WebApi", "App_Data", "Form", n + ".xml")).ToArray();
            Assert.All(samples, f => Assert.True(File.Exists(f), f));
            foreach (string file in samples)
            {
                FormValidation result = FormDefinitionValidator.Validate(Path.GetFileNameWithoutExtension(file), FormDefinitionValidator.Normalize(File.ReadAllText(file)));
                Assert.True(result.IsValid, $"{Path.GetFileName(file)}: {string.Join("; ", result.Errors.Select(e => e.Message))}");
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("setsale")]
        [InlineData("SetSale")]
        [InlineData("set-Sale")]
        [InlineData("../setSale")]
        [InlineData("setSaleInvoiceThatIsMuchTooLongForTheColumn")]
        [InlineData("getAuth")]
        public void Bad_names_are_rejected(string name)
        {
            Assert.False(FormDefinitionValidator.Validate(name, Valid).IsValid);
        }

        [Fact]
        public void Malformed_xml_reports_a_position_and_loader_rules_are_enforced()
        {
            FormValidation broken = FormDefinitionValidator.Validate("getCustomer", Valid.Replace("</import>", ""));
            FormIssue issue = Assert.Single(broken.Errors);
            Assert.True(issue.Line > 0);

            Assert.False(FormDefinitionValidator.Validate("getCustomer", Valid.Replace("type=\"List\"", "type=\"Khac\"")).IsValid);
            Assert.False(FormDefinitionValidator.Validate("getCustomer", Valid.Replace("<query action=\"processing\">", "<query action=\"khac\">")).IsValid);
            Assert.False(FormDefinitionValidator.Validate("getCustomer", "<root/>").IsValid);
            Assert.False(FormDefinitionValidator.Validate("getCustomer", new string(' ', FormDefinitionValidator.MaxBytes + 1)).IsValid);
        }

        [Fact]
        public void External_entities_and_entity_bombs_are_refused_and_nothing_is_leaked()
        {
            string file = Path.Combine(Path.GetTempPath(), "secret-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, "NOI-DUNG-BI-MAT");
            try
            {
                string uri = new Uri(file).AbsoluteUri;
                string xxe = $"<?xml version=\"1.0\"?>\n<!DOCTYPE import [<!ENTITY x SYSTEM \"{uri}\">]>\n" + Valid[Valid.IndexOf("<import", StringComparison.Ordinal)..].Replace("description=\"Mô tả form\"", "description=\"&x;\"");
                FormValidation result = FormDefinitionValidator.Validate("getCustomer", xxe);
                Assert.False(result.IsValid);
                Assert.DoesNotContain("NOI-DUNG-BI-MAT", string.Join(" ", result.Errors.Select(e => e.Message)));
            }
            finally
            {
                File.Delete(file);
            }

            Assert.False(FormDefinitionValidator.Validate("getCustomer", "<?xml version=\"1.0\"?>\n<!DOCTYPE import SYSTEM \"http://evil.example/x.dtd\">\n" + Valid[Valid.IndexOf("<import", StringComparison.Ordinal)..]).IsValid);

            string bomb = "<?xml version=\"1.0\"?>\n<!DOCTYPE import [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">"
                + "<!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\"><!ENTITY e \"&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;\">]>\n"
                + Valid[Valid.IndexOf("<import", StringComparison.Ordinal)..].Replace("description=\"Mô tả form\"", "description=\"&e;\"");
            Assert.False(FormDefinitionValidator.Validate("getCustomer", bomb).IsValid);

            // Entity nội bộ đơn giản (như &And; trong form thật) vẫn dùng được
            string internalEntity = "<?xml version=\"1.0\"?>\n<!DOCTYPE import [<!ENTITY Mota \"Mo ta\">]>\n" + Valid[Valid.IndexOf("<import", StringComparison.Ordinal)..].Replace("description=\"Mô tả form\"", "description=\"&Mota;\"");
            Assert.True(FormDefinitionValidator.Validate("getCustomer", internalEntity).IsValid);
        }

        [Fact]
        public void Server_level_sql_is_blocked_but_comments_temp_tables_and_dynamic_sql_are_handled_sensibly()
        {
            foreach (string sql in new[] { "EXEC master..xp_cmdshell 'dir'", "EXEC sp_OACreate 'x', @o OUT", "SELECT * FROM OPENROWSET('SQLNCLI','x','select 1')", "BULK INSERT dmkh FROM 'c:\\x.txt'",
                "SELECT 1; GRANT CONTROL TO public", "DROP TABLE dmkh", "TRUNCATE TABLE dmkh", "CREATE LOGIN x WITH PASSWORD='y'", "ALTER TABLE dmkh ADD x INT", "SHUTDOWN", "WAITFOR DELAY '00:01'" })
            {
                FormValidation result = FormDefinitionValidator.Validate("getCustomer", WithSql(sql));
                Assert.False(result.IsValid, sql);
                Assert.Contains(result.Errors, e => e.Message.StartsWith("SQL bị chặn"));
            }

            Assert.True(FormDefinitionValidator.Validate("getCustomer", WithSql("-- xp_cmdshell chỉ nằm trong ghi chú\nSELECT 1 AS customerId /* DROP TABLE dmkh */")).IsValid);
            Assert.True(FormDefinitionValidator.Validate("getCustomer", WithSql("CREATE TABLE #t (a INT) INSERT #t VALUES (1) SELECT a AS customerId FROM #t DROP TABLE #t")).IsValid);

            FormValidation dynamicSql = FormDefinitionValidator.Validate("getCustomer", WithSql("DECLARE @s NVARCHAR(100) = N'SELECT 1' EXEC (@s)"));
            Assert.True(dynamicSql.IsValid);
            Assert.NotEmpty(dynamicSql.Warnings);
        }

        // ---------- Gói ----------
        private static (byte[] Zip, string Sha) PackageOf(params string[] names) =>
            FormPackage.Build("DEMO", 7, new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc), names.Select((n, i) => (n, i + 1, Valid)));

        [Fact]
        public void Package_round_trips_and_its_manifest_hash_is_stable()
        {
            var (zip, sha) = PackageOf("getCustomer", "getItem");

            PackageContents back = FormPackage.Read(zip);

            Assert.Equal(sha, back.ManifestSha256);
            Assert.Equal(["getCustomer", "getItem"], back.Manifest.Forms.Select(f => f.Name));
            Assert.Equal(Valid, back.Files["getItem"]);
            Assert.Equal("DEMO", back.Manifest.Project);
        }

        private static byte[] Mutate(byte[] zip, Action<ZipArchive> change)
        {
            using MemoryStream stream = new();
            stream.Write(zip);
            using (ZipArchive archive = new(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                change(archive);
            }
            return stream.ToArray();
        }

        private static void Replace(ZipArchive archive, string name, string text)
        {
            archive.GetEntry(name)?.Delete();
            using StreamWriter writer = new(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        [Fact]
        public void Tampering_extra_entries_traversal_and_oversize_entries_are_rejected()
        {
            var (zip, _) = PackageOf("getCustomer");

            FormPackageException changed = Assert.Throws<FormPackageException>(() => FormPackage.Read(Mutate(zip, a => Replace(a, "forms/getCustomer.xml", Valid + "<!-- đã sửa -->"))));
            Assert.Equal("HASH_MISMATCH", changed.Code);

            Assert.Throws<FormPackageException>(() => FormPackage.Read(Mutate(zip, a => Replace(a, "forms/evil.xml", "x"))));
            Assert.Throws<FormPackageException>(() => FormPackage.Read(Mutate(zip, a => Replace(a, "forms/../../outside.xml", "x"))));
            Assert.Throws<FormPackageException>(() => FormPackage.Read(Mutate(zip, a => Replace(a, "forms/setOther.xml", Valid))));   // thừa so với manifest
            Assert.Throws<FormPackageException>(() => FormPackage.Read(Mutate(zip, a => Replace(a, "forms/getCustomer.xml", new string('a', FormDefinitionValidator.MaxBytes + 10)))));
            Assert.Throws<FormPackageException>(() => FormPackage.Read(Mutate(zip, a => a.GetEntry("manifest.json")!.Delete())));
            Assert.Throws<FormPackageException>(() => FormPackage.Read(Encoding.UTF8.GetBytes("khong phai zip")));
            Assert.Throws<FormPackageException>(() => FormPackage.Build("DEMO", 1, DateTime.UtcNow, []));
        }

        [Fact]
        public void Many_large_entries_are_refused_before_they_exhaust_memory()
        {
            // 200 form × 200 KB = 40 MB sau giải nén (zip rất nhỏ vì toàn dấu cách) vượt giới hạn 32 MB của gói
            string big = Valid.Replace("</import>", new string(' ', 200 * 1024) + "</import>");
            var (zip, _) = FormPackage.Build("DEMO", 1, DateTime.UtcNow, Enumerable.Range(0, 200).Select(i => ($"getBig{i}", 1, big)));
            Assert.True(zip.Length < FormPackage.MaxTotalBytes);

            FormPackageException ex = Assert.Throws<FormPackageException>(() => FormPackage.Read(zip));
            Assert.Equal("INVALID_PACKAGE", ex.Code);
        }

        // ---------- Áp gói ----------
        private static string TempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "formpkg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        [Fact]
        public void Dry_run_writes_nothing_and_a_wrong_manifest_hash_stops_before_anything_happens()
        {
            var (zip, sha) = PackageOf("getCustomer");
            string target = TempDir();
            try
            {
                ApplyResult dry = FormPackageApplier.Run(zip, sha, target, apply: false, "PROD", "nguyen", TimeProvider.System);
                Assert.True(dry.Success);
                Assert.False(dry.Applied);
                Assert.Equal("NEW", Assert.Single(dry.Files).Outcome);
                Assert.Empty(Directory.GetFileSystemEntries(target));

                FormPackageException wrong = Assert.Throws<FormPackageException>(() => FormPackageApplier.Run(zip, new string('0', 64), target, apply: true, "PROD", "nguyen", TimeProvider.System));
                Assert.Equal("MANIFEST_HASH_MISMATCH", wrong.Code);
                Assert.Empty(Directory.GetFileSystemEntries(target));
            }
            finally
            {
                Directory.Delete(target, recursive: true);
            }
        }

        [Fact]
        public void Apply_writes_new_files_backs_up_changed_ones_skips_same_and_leaves_no_temp_files()
        {
            string target = TempDir();
            try
            {
                string changedOld = Valid.Replace("Mô tả form", "Bản cũ");
                File.WriteAllText(Path.Combine(target, "getCustomer.xml"), changedOld);
                File.WriteAllText(Path.Combine(target, "getItem.xml"), Valid);   // giống hệt gói
                var (zip, sha) = PackageOf("getCustomer", "getItem", "getSupplier");

                ApplyResult result = FormPackageApplier.Run(zip, sha, target, apply: true, "PROD", "nguyen", TimeProvider.System);

                Assert.True(result.Success && result.Applied);
                Assert.Equal(["CHANGED", "SAME", "NEW"], result.Files.Select(f => f.Outcome));
                Assert.Equal(Valid, File.ReadAllText(Path.Combine(target, "getCustomer.xml")));
                Assert.True(File.Exists(Path.Combine(target, "getSupplier.xml")));
                string backup = Directory.GetFiles(Path.Combine(target, FormPackageApplier.BackupFolder), "getCustomer.xml", SearchOption.AllDirectories).Single();
                Assert.Equal(changedOld, File.ReadAllText(backup));
                Assert.Empty(Directory.GetFiles(target, "*.tmp"));

                // Áp lần hai: không còn gì thay đổi
                ApplyResult again = FormPackageApplier.Run(zip, sha, target, apply: true, "PROD", "nguyen", TimeProvider.System);
                Assert.All(again.Files, f => Assert.Equal("SAME", f.Outcome));
            }
            finally
            {
                Directory.Delete(target, recursive: true);
            }
        }

        [Fact]
        public void One_invalid_form_stops_the_whole_package_and_case_only_duplicates_cannot_be_packaged()
        {
            string target = TempDir();
            try
            {
                // Gói chứa một form sai (xp_cmdshell) cạnh một form tốt: không ghi form tốt
                var (bad, badSha) = FormPackage.Build("DEMO", 1, DateTime.UtcNow, [("getCustomer", 1, Valid), ("getItem", 1, WithSql("EXEC xp_cmdshell 'x'"))]);
                ApplyResult rejected = FormPackageApplier.Run(bad, badSha, target, apply: true, "PROD", "nguyen", TimeProvider.System);
                Assert.False(rejected.Success);
                Assert.False(rejected.Applied);
                Assert.Equal("FAILED", rejected.Files.Single(f => f.Name == "getItem").Outcome);
                Assert.Empty(Directory.GetFileSystemEntries(target));

                // Hai form chỉ khác chữ hoa/thường là cùng một file trên Windows: không được lập gói
                FormPackageException clash = Assert.Throws<FormPackageException>(() => FormPackage.Build("DEMO", 2, DateTime.UtcNow, [("setSaleInvoice", 1, Valid), ("setSALEInvoice", 1, Valid)]));
                Assert.Equal("INVALID_PACKAGE", clash.Code);
            }
            finally
            {
                Directory.Delete(target, recursive: true);
            }
        }

        [Fact]
        public void Result_json_has_no_form_content_or_secrets()
        {
            var (zip, sha) = PackageOf("getCustomer");
            string target = TempDir();
            try
            {
                string json = FormPackageApplier.ResultToJson(FormPackageApplier.Run(zip, sha, target, apply: false, "DEV", "nguyen", TimeProvider.System));

                Assert.Contains("\"packageId\": 7", json);
                Assert.Contains(sha, json);
                Assert.DoesNotContain("SELECT", json);
                Assert.DoesNotContain(target, json);
            }
            finally
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }
}
