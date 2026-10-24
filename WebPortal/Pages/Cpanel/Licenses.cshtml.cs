using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class LicensesModel(IPortalStore store, LicenseService licenses, LicenseKeyProvider keys) : CpanelPageModel
    {
        public const int MaxManifestBytes = 4 * 1024 * 1024;

        public override string NavKey => "licenses";

        public LicenseKeyStatus Key { get; private set; } = null!;
        public IReadOnlyList<LicenseRecord> Licenses { get; private set; } = [];
        private Dictionary<int, string> _codes = [];

        public string Code(int projectId) => _codes.GetValueOrDefault(projectId, "#" + projectId);

        public async Task OnGetAsync()
        {
            Key = keys.Status();
            OpResult result = await licenses.ListAsync(Identity!, null, Aborted);
            Licenses = result.Success ? (IReadOnlyList<LicenseRecord>)result.Data! : [];
            _codes = (await store.ListProjectsAsync(Aborted)).ToDictionary(p => p.Id, p => p.MaDa);
        }

        public async Task<IActionResult> OnPostGenerateAsync()
        {
            if (keys.Generate())
            {
                await store.WriteAuditAsync(PortalGuard.Audit(Identity, null, "license.key.generate", keys.Signer?.Kid, "SUCCESS", null, ClientIp), Aborted);
                Flash = "Đã tạo cặp khóa. Nhúng public key vào bản phát hành trước khi cấp license.";
            }
            else
            {
                FlashError = "Không tạo được: cần khai Portal:License:KeyPath và KeyPassword (≥ 12 ký tự), và file chưa tồn tại.";
            }
            return Redirect("/cpanel/licenses");
        }

        [RequestSizeLimit(MaxManifestBytes)]
        public async Task<IActionResult> OnPostSignAsync(IFormFile? manifest)
        {
            if (manifest == null || manifest.Length is 0 or > MaxManifestBytes)
            {
                FlashError = Describe("INVALID_MANIFEST");
                return Redirect("/cpanel/licenses");
            }
            using MemoryStream memory = new();
            await manifest.CopyToAsync(memory, Aborted);
            OpResult result = await licenses.SignIntegrityAsync(Identity!, memory.ToArray(), ClientIp, Aborted);
            if (!result.Success)
            {
                FlashError = Describe(result.Code);
                return Redirect("/cpanel/licenses");
            }
            return File((byte[])result.Data!, "application/json", Security.Licensing.IntegrityManifest.SignatureFile);
        }

        public async Task<IActionResult> OnPostRevokeAsync(Guid licenseId, string? reason)
        {
            Report(await licenses.RevokeAsync(Identity!, licenseId, null, reason, ClientIp, Aborted), "Đã đánh dấu thu hồi (không cấp lại cho máy này).");
            return Redirect("/cpanel/licenses");
        }
    }
}
