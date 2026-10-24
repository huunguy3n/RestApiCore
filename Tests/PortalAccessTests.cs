using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Security.Licensing;
using Tests.Support;
using WebPortal.Portal;

namespace Tests
{
    public class PortalGuardTests
    {
        private static PortalIdentity Sa() => new(AccountKind.SuperAdmin, 1, "sa", "SA", null, null, Guid.NewGuid());
        private static PortalIdentity Admin(int projectId, string maDa = "AQTC") =>
            new(AccountKind.Admin, 2, "admin." + maDa.ToLowerInvariant(), "Admin", projectId, maDa, Guid.NewGuid());
        private static PortalIdentity Api(int projectId, string maDa = "AQTC") =>
            new(AccountKind.Api, 9, "doitac01", "Đối tác", projectId, maDa, Guid.NewGuid());

        [Fact]
        public void CanAdmin_SaMoiDuAn_AdminChiDungDuAn_ApiKhongDuoc()
        {
            Assert.True(PortalGuard.CanAdmin(Sa(), 3));
            Assert.True(PortalGuard.CanAdmin(Admin(3), 3));
            Assert.False(PortalGuard.CanAdmin(Admin(3), 4));
            Assert.False(PortalGuard.CanAdmin(Api(3), 3));
            Assert.False(PortalGuard.CanAdmin(null, 3));
        }

        [Fact]
        public void IsApiOf_ChiDungDuAnCuaMinh()
        {
            Assert.True(PortalGuard.IsApiOf(Api(3), 3));
            Assert.False(PortalGuard.IsApiOf(Api(3), 4));
            Assert.False(PortalGuard.IsApiOf(Admin(3), 3));
            Assert.False(PortalGuard.IsApiOf(Sa(), 3));
        }

        [Fact]
        public void Audit_SaLamHoDuAn_GhiActingAs()
        {
            AuditRecord asProject = PortalGuard.Audit(Sa(), 3, "apiuser.create", "DOITAC01", "SUCCESS");
            Assert.Equal(AccountKind.SuperAdmin, asProject.ActingAs);
            Assert.Equal(3, asProject.ProjectId);

            AuditRecord own = PortalGuard.Audit(Admin(3), 3, "apiuser.create", "DOITAC01", "SUCCESS");
            Assert.Null(own.ActingAs);
        }
    }

    public class ProjectCodeAndLoginTests
    {
        [Theory]
        [InlineData("AQTC", true)]
        [InlineData("a_1-Z", true)]
        [InlineData("cpanel", false)]
        [InlineData("login", false)]
        [InlineData("css", false)]
        [InlineData("js", false)]
        [InlineData("lib", false)]
        [InlineData("samples", false)]
        [InlineData("error", false)]
        [InlineData("session", false)]
        [InlineData("api", false)]
        [InlineData("", false)]
        [InlineData("co dau", false)]
        [InlineData(null, false)]
        public void ProjectCode_LoaiDoanUrlDanhRieng(string? code, bool expected)
        {
            Assert.Equal(expected, ProjectCode.IsValid(code));
        }

        [Fact]
        public void RouteConstraint_ChiNhanMaHopLe()
        {
            ProjectCodeRouteConstraint constraint = new();
            RouteValueDictionary values = new();

            values["maDa"] = "AQTC";
            Assert.True(constraint.Match(null, null, "maDa", values, RouteDirection.IncomingRequest));

            values["maDa"] = "cpanel";
            Assert.False(constraint.Match(null, null, "maDa", values, RouteDirection.IncomingRequest));
        }

        [Theory]
        [InlineData("DOITAC01", true)]
        [InlineData("a.b_c@d-e", true)]
        [InlineData("ab", false)]
        [InlineData("có dấu", false)]
        [InlineData("", false)]
        public void LoginName_KhopRestApiUser(string? login, bool expected)
        {
            Assert.Equal(expected, LoginName.IsValid(login));
        }
    }

    public class ApiUserRightsParseTests
    {
        [Fact]
        public void ParseRights_Rong_LaTatCa()
        {
            var (rights, error) = ApiUserService.ParseRights("", "");
            Assert.Null(error);
            Assert.Empty(rights!.Value.Forms);
            Assert.Empty(rights.Value.Ips);
            Assert.Equal("tất cả", ApiUserService.Describe(rights.Value.Forms));
        }

        [Fact]
        public void ParseRights_CoKhai_GiuDanhSach()
        {
            var (rights, error) = ApiUserService.ParseRights("getCustomer, setSaleInvoice", "192.168.1.255 văn phòng\n10.0.0.1");
            Assert.Null(error);
            Assert.Equal(["getCustomer", "setSaleInvoice"], rights!.Value.Forms);
            Assert.Equal(2, rights.Value.Ips.Count);
            Assert.Equal("văn phòng", rights.Value.Ips[0].Description);
            Assert.Equal("getCustomer, setSaleInvoice", ApiUserService.Describe(rights.Value.Forms));
        }

        [Fact]
        public void ParseRights_TenFormSai_InvalidFormName()
        {
            Assert.Equal("INVALID_FORM_NAME", ApiUserService.ParseRights("get Customer", "").Error);
        }
    }

    public class ProjectFolderTests
    {
        [Fact]
        public void IsAllowedFolder_NamDuoiGoc_KhongCoChamCham()
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "restapi-roots", "CustomerPro"));
            Directory.CreateDirectory(root);
            var resolver = new ProjectConnectionResolver(new PortalSettings { AllowedApiRoots = [root] });
            string inside = Path.Combine(root, "AQTC", "restApi");

            Assert.True(resolver.IsAllowedFolder(inside));
            Assert.False(resolver.IsAllowedFolder(Path.Combine(root, "..", "Other")));
            Assert.False(resolver.IsAllowedFolder(@"AQTC\restApi"));
            Assert.False(resolver.IsAllowedFolder(Path.Combine(Path.GetTempPath(), "khac", "restApi")));
        }
    }

    public class ProjectSyncIdorTests
    {
        private static PortalApiUser User(int id, int projectId) =>
            new(id, projectId, "DOITAC01", "NVKD01", "Đối tác", "hash", true, 1, 0, null, null, [], []);

        [Fact]
        public async Task Push_UserCuaDuAnKhac_NotFound_KhongGoiFolder()
        {
            MemoryPortalStore store = new();
            store.Users[5] = User(5, 99);
            var sync = new ProjectSyncService(store, new ThrowingResolver(), NullLogger<ProjectSyncService>.Instance);
            PortalProject project = new(3, "AQTC", @"D:\x", "", true, null, null);

            OpResult missing = await sync.PushAsync(project, 5);
            OpResult unknown = await sync.PushAsync(project, 404);

            Assert.Equal("NOT_FOUND", missing.Code);
            Assert.Equal("NOT_FOUND", unknown.Code);
        }

        private sealed class ThrowingResolver : IProjectConnectionResolver
        {
            public ProjectConnection Resolve(PortalProject project) => throw new InvalidOperationException("Không được gọi Resolve khi IDOR");
            public Task<FolderCheck> CheckAsync(string apiFolder, string maDa, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
            public bool IsAllowedFolder(string apiFolder) => false;
        }
    }

    public class LicenseServiceAccessTests
    {
        private static PortalIdentity Sa() => new(AccountKind.SuperAdmin, 1, "sa", "SA", null, null, Guid.NewGuid());
        private static PortalIdentity Admin(int projectId) => new(AccountKind.Admin, 2, "admin.aqtc", "Admin", projectId, "AQTC", Guid.NewGuid());
        private static PortalIdentity Api() => new(AccountKind.Api, 9, "doitac01", "Đối tác", 3, "AQTC", Guid.NewGuid());
        private static PortalProject Project(int id = 3, string maDa = "AQTC") => new(id, maDa, @"D:\x", "", true, null, null);

        private static (LicenseService Service, MemoryPortalStore Store, LicenseSigner Signer) Create()
        {
            string dir = Path.Combine(Path.GetTempPath(), "restapi-lickey-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "license.pfx");
            const string password = "test-license-12";
            using (ECDsa key = LicenseSigner.GenerateKey())
            {
                CertificateRequest request = new("CN=Fast RestApi License Test", key, HashAlgorithmName.SHA256);
                using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
            }
            var keys = new LicenseKeyProvider(new LicenseKeySettings { KeyPath = path, KeyPassword = password }, NullLogger<LicenseKeyProvider>.Instance);
            MemoryPortalStore store = new();
            return (new LicenseService(store, keys, TimeProvider.System), store, keys.Signer!);
        }

        [Fact]
        public async Task Issue_AdminDuAnKhac_Forbidden()
        {
            var (service, store, _) = Create();

            OpResult result = await service.IssueAsync(Admin(99), Project(), RequestJson(), null, "10.0.0.1");

            Assert.Equal("FORBIDDEN", result.Code);
            Assert.Empty(store.Licenses);
        }

        [Fact]
        public async Task Issue_UserApi_Forbidden()
        {
            Assert.Equal("FORBIDDEN", (await Create().Service.IssueAsync(Api(), Project(), RequestJson(), null, "10.0.0.1")).Code);
        }

        [Fact]
        public async Task Issue_FileYeuCauCuaDuAnKhac_WrongProject()
        {
            var (service, store, _) = Create();

            OpResult result = await service.IssueAsync(Admin(3), Project(), RequestJson("KHAC"), null, "10.0.0.1");

            Assert.Equal("WRONG_PROJECT", result.Code);
            Assert.Empty(store.Licenses);
            Assert.Contains(store.Audits, a => a.Outcome == "REJECTED");
        }

        [Fact]
        public async Task Issue_Sa_KyDuocVaGhiPortalLicense()
        {
            var (service, store, signer) = Create();

            OpResult result = await service.IssueAsync(Sa(), Project(), RequestJson(), null, "10.0.0.1");

            Assert.True(result.Success);
            LicenseFile file = Assert.IsType<LicenseFile>(result.Data);
            Assert.Equal(LicenseRules.FileName, file.FileName);
            Assert.Equal(signer.Kid, file.Payload.Kid);
            Assert.Single(store.Licenses);
            LicenseCheck check = new LicenseVerifier(new Dictionary<string, byte[]> { [signer.Kid] = signer.PublicKey })
                .Verify(file.Content, "AQTC", ["AA-BB-CC-DD-EE-01"], DateTimeOffset.UtcNow);
            Assert.True(check.IsValid);
        }

        [Fact]
        public async Task ListVaRevoke_AdminKhongXemDuAnKhac()
        {
            var (service, _, _) = Create();

            Assert.Equal("FORBIDDEN", (await service.ListAsync(Admin(3), 99)).Code);
            Assert.Equal("FORBIDDEN", (await service.RevokeAsync(Admin(3), Guid.NewGuid(), 99, "x", "10.0.0.1")).Code);
            Assert.True((await service.ListAsync(Admin(3), 3)).Success);
        }

        [Fact]
        public async Task SignIntegrity_ChiSa()
        {
            var (service, _, _) = Create();
            IntegrityDocument document = new(1, "restApi", "1.0.0", DateTimeOffset.UtcNow, [new IntegrityEntry("a.dll", "00")]);
            byte[] manifest = IntegrityManifest.Serialize(document);

            Assert.Equal("FORBIDDEN", (await service.SignIntegrityAsync(Admin(3), manifest, "10.0.0.1")).Code);
            Assert.True((await service.SignIntegrityAsync(Sa(), manifest, "10.0.0.1")).Success);
        }

        private static string RequestJson(string project = "AQTC") =>
            LicenseRequest.Create(project, ["AA-BB-CC-DD-EE-01"], "PROD-01", "1.0.0", DateTimeOffset.UtcNow).ToJson();
    }
}
