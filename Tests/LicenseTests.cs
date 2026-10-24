using System.Security.Cryptography;
using System.Text.Json;
using Models;
using Security.Licensing;
using Web.Custom;

namespace Tests
{
    public class LicenseTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        private static readonly string[] Macs = ["AA-BB-CC-DD-EE-01", "AA-BB-CC-DD-EE-02"];

        private static (LicenseSigner Signer, LicenseVerifier Verifier) Pair()
        {
            LicenseSigner signer = new(LicenseSigner.GenerateKey());
            return (signer, new LicenseVerifier(new Dictionary<string, byte[]>(StringComparer.Ordinal) { [signer.Kid] = signer.PublicKey }));
        }

        private static LicenseRequest Request(string project = "AQTC") =>
            LicenseRequest.Create(project, Macs, "PROD-01", "1.0.0", Now);

        [Fact]
        public void IssueVaVerify_DuDieuKien_HopLe()
        {
            var (signer, verifier) = Pair();
            var (_, text) = signer.Issue(Request(), Now, null);

            LicenseCheck check = verifier.Verify(text, "AQTC", Macs, Now);

            Assert.True(check.IsValid);
            Assert.Equal(LicenseCodes.Valid, check.Code);
            Assert.Equal(signer.Kid, check.Payload!.Kid);
            Assert.Null(check.Payload.ExpiresAt);
        }

        [Fact]
        public void Verify_ChuKyGia_BadSignature()
        {
            var (signer, verifier) = Pair();
            var (_, text) = signer.Issue(Request(), Now, null);
            LicenseEnvelope envelope = JsonSerializer.Deserialize<LicenseEnvelope>(text)!;
            byte[] fake = new byte[64];
            RandomNumberGenerator.Fill(fake);
            string tampered = JsonSerializer.Serialize(envelope with { Signature = Convert.ToBase64String(fake) });

            Assert.Equal(LicenseCodes.BadSignature, verifier.Verify(tampered, "AQTC", Macs, Now).Code);
        }

        [Fact]
        public void Verify_SuaPayloadGiuChuKy_BadSignature()
        {
            var (signer, verifier) = Pair();
            var (payload, text) = signer.Issue(Request(), Now, null);
            LicenseEnvelope envelope = JsonSerializer.Deserialize<LicenseEnvelope>(text)!;
            LicensePayload swapped = payload with { ProjectCode = "KHAC" };
            string tampered = JsonSerializer.Serialize(envelope with
            {
                Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(swapped))
            });

            Assert.Equal(LicenseCodes.BadSignature, verifier.Verify(tampered, "KHAC", Macs, Now).Code);
        }

        [Fact]
        public void Verify_KhoaKhongTinCay_UnknownKey()
        {
            LicenseSigner other = new(LicenseSigner.GenerateKey());
            var (_, text) = other.Issue(Request(), Now, null);

            Assert.Equal(LicenseCodes.UnknownKey, Pair().Verifier.Verify(text, "AQTC", Macs, Now).Code);
        }

        [Fact]
        public void Verify_SaiMac_WrongMachine()
        {
            var (signer, verifier) = Pair();
            var (_, text) = signer.Issue(Request(), Now, null);

            Assert.Equal(LicenseCodes.WrongMachine, verifier.Verify(text, "AQTC", ["11-22-33-44-55-66"], Now).Code);
        }

        [Fact]
        public void Verify_MotMacKhopTrongNhieuCard_HopLe()
        {
            var (signer, verifier) = Pair();
            var (_, text) = signer.Issue(Request(), Now, null);

            Assert.True(verifier.Verify(text, "AQTC", ["00-00-00-00-00-01", Macs[1]], Now).IsValid);
        }

        [Fact]
        public void Verify_SaiMaDuAn_WrongProject()
        {
            var (signer, verifier) = Pair();
            var (_, text) = signer.Issue(Request(), Now, null);

            Assert.Equal(LicenseCodes.WrongProject, verifier.Verify(text, "KHAC", Macs, Now).Code);
        }

        [Fact]
        public void Verify_HetHan_Expired()
        {
            var (signer, verifier) = Pair();
            var (_, text) = signer.Issue(Request(), Now, TimeSpan.FromDays(1));

            Assert.True(verifier.Verify(text, "AQTC", Macs, Now.AddHours(23)).IsValid);
            Assert.Equal(LicenseCodes.Expired, verifier.Verify(text, "AQTC", Macs, Now.AddDays(1)).Code);
            Assert.Equal(LicenseCodes.Expired, verifier.Verify(text, "AQTC", Macs, Now.AddDays(2)).Code);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Verify_ThieuFile_Missing(string? text)
        {
            Assert.Equal(LicenseCodes.Missing, Pair().Verifier.Verify(text, "AQTC", Macs, Now).Code);
        }

        [Theory]
        [InlineData("{")]
        [InlineData("{\"kid\":\"x\"}")]
        [InlineData("khong-phai-json")]
        public void Verify_SaiDinhDang_Malformed(string text)
        {
            Assert.Equal(LicenseCodes.Malformed, Pair().Verifier.Verify(text, "AQTC", Macs, Now).Code);
        }

        [Fact]
        public void LicenseRequest_Parse_TuChoiMacSaiVaMaDuAnSai()
        {
            LicenseRequest ok = Request();
            Assert.NotNull(LicenseRequest.Parse(ok.ToJson()));
            Assert.Null(LicenseRequest.Parse((ok with { ProjectCode = "" }).ToJson()));
            Assert.Null(LicenseRequest.Parse((ok with { Macs = ["aa-bb-cc-dd-ee-01"] }).ToJson()));
            Assert.Null(LicenseRequest.Parse(null));
        }

        [Theory]
        [InlineData("AQTC", true)]
        [InlineData("a_1-Z", true)]
        [InlineData("", false)]
        [InlineData("co dau", false)]
        [InlineData(null, false)]
        public void LicenseRules_MaDuAn(string? code, bool expected)
        {
            Assert.Equal(expected, LicenseRules.IsValidProjectCode(code));
        }

        [Fact]
        public void MachineFingerprint_NormalizeVaKiemMac()
        {
            Assert.Equal("0A-1B-2C-3D-4E-5F", MachineFingerprint.Normalize([0x0A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F]));
            Assert.Null(MachineFingerprint.Normalize([0, 0, 0, 0, 0, 0]));
            Assert.Null(MachineFingerprint.Normalize([1, 2, 3, 4, 5]));
            Assert.True(MachineFingerprint.IsValidMac("AA-BB-CC-DD-EE-FF"));
            Assert.False(MachineFingerprint.IsValidMac("aa-bb-cc-dd-ee-ff"));
            Assert.False(MachineFingerprint.IsValidMac("AA:BB:CC:DD:EE:FF"));
        }

        [Fact]
        public void IntegrityManifest_ChuKyDungRoiSuaFileThiChanged()
        {
            var (signer, verifier) = Pair();
            string dir = Path.Combine(Path.GetTempPath(), "restapi-integrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "a.dll"), "bin-1");
                IntegrityDocument document = IntegrityManifest.Create(dir, "restApi", "1.0.0", Now);
                byte[] json = IntegrityManifest.Serialize(document);
                File.WriteAllBytes(Path.Combine(dir, IntegrityManifest.ManifestFile), json);
                File.WriteAllText(Path.Combine(dir, IntegrityManifest.SignatureFile), signer.SignDetached(json));

                Assert.True(IntegrityManifest.Verify(dir, verifier).IsValid);

                File.WriteAllText(Path.Combine(dir, "a.dll"), "bin-2");
                IntegrityResult changed = IntegrityManifest.Verify(dir, verifier);
                Assert.Equal(IntegrityManifest.Changed, changed.Code);
                Assert.Contains(changed.Problems, p => p.Contains("a.dll", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void IntegrityManifest_ChuKyGia_BadSignature()
        {
            var (_, verifier) = Pair();
            LicenseSigner other = new(LicenseSigner.GenerateKey());
            string dir = Path.Combine(Path.GetTempPath(), "restapi-integrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "a.dll"), "bin");
                IntegrityDocument document = IntegrityManifest.Create(dir, "restApi", "1.0.0", Now);
                byte[] json = IntegrityManifest.Serialize(document);
                File.WriteAllBytes(Path.Combine(dir, IntegrityManifest.ManifestFile), json);
                File.WriteAllText(Path.Combine(dir, IntegrityManifest.SignatureFile), other.SignDetached(json));

                Assert.Equal(IntegrityManifest.BadSignature, IntegrityManifest.Verify(dir, verifier).Code);
                File.Delete(Path.Combine(dir, IntegrityManifest.SignatureFile));
                Assert.Equal(IntegrityManifest.Missing, IntegrityManifest.Verify(dir, verifier).Code);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void LicenseState_DevKhongKiem_ProdThieuMaDuAnLaSai()
        {
            Assert.False(LicenseState.NotChecked().Checked);
            Assert.True(LicenseState.NotChecked().IsValid);

            LicenseVerifier verifier = Pair().Verifier;
            LicenseGuard guard = new(verifier, () => Macs, TimeProvider.System, clockFile: null);

            LicenseState dev = LicenseState.Evaluate(new AppConfig { environment = "DEV", projectCode = "AQTC" }, ".", guard, verifier);
            Assert.False(dev.Checked);
            Assert.True(dev.IsValid);

            LicenseState prod = LicenseState.Evaluate(new AppConfig { environment = "PROD", projectCode = "" }, ".", guard, verifier);
            Assert.True(prod.Checked);
            Assert.False(prod.IsValid);
            Assert.Equal(LicenseCodes.WrongProject, prod.License.Code);
        }
    }
}
