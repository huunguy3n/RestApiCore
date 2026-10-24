using System.Text;
using Microsoft.AspNetCore.Mvc;
using Security.Licensing;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class LicensesModel(LicenseService licenses, LicenseKeyProvider keys, LicenseKeySettings settings) : ProjectPageModel
    {
        public override string NavKey => "licenses";

        public IReadOnlyList<LicenseRecord> Licenses { get; private set; } = [];
        public bool KeyReady { get; private set; }
        public int DefaultDays => settings.DefaultValidDays;

        private string Self => $"/{MaDa}/licenses";

        public async Task<IActionResult> OnGetAsync()
        {
            OpResult result = await licenses.ListAsync(Identity!, Project.Id, Aborted);
            if (!result.Success)
            {
                return Denied();
            }
            Licenses = (IReadOnlyList<LicenseRecord>)result.Data!;
            KeyReady = keys.Signer != null;
            return Page();
        }

        [RequestSizeLimit(64 * 1024)]
        public async Task<IActionResult> OnPostIssueAsync(IFormFile? request, int? validDays)
        {
            string? text = null;
            if (request is { Length: > 0 and <= LicenseRequest.MaxBytes })
            {
                using StreamReader reader = new(request.OpenReadStream(), Encoding.UTF8);
                text = await reader.ReadToEndAsync(Aborted);
            }
            OpResult result = await licenses.IssueAsync(Identity!, Project, text, validDays, ClientIp, Aborted);
            if (!result.Success)
            {
                FlashError = Describe(result.Code);
                return Redirect(Self);
            }
            LicenseFile file = (LicenseFile)result.Data!;
            return File(Encoding.UTF8.GetBytes(file.Content), "application/json", file.FileName);
        }

        public async Task<IActionResult> OnPostRevokeAsync(Guid licenseId, string? reason)
        {
            Report(await licenses.RevokeAsync(Identity!, licenseId, Project.Id, reason, ClientIp, Aborted), "Đã đánh dấu thu hồi.");
            return Redirect(Self);
        }
    }
}
