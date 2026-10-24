using System.Text;
using Business;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class PackagesModel(FormManagementService forms) : ProjectPageModel
    {
        public override string NavKey => "packages";

        public IReadOnlyList<PackageInfo> Packages { get; private set; } = [];
        public IReadOnlyList<SyncLogEntry> SyncLog { get; private set; } = [];
        public ApplyResult? Applied { get; private set; }

        private string Self => $"/{MaDa}/packages";

        public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : Denied();

        public async Task<IActionResult> OnPostCreateAsync()
        {
            OpResult result = await forms.CreatePackageAsync(Identity!, Project, Aborted);
            if (result.Success)
            {
                Flash = $"Đã tạo gói #{result.Data}.";
            }
            else
            {
                FlashError = result.Code switch
                {
                    "INVALID_FORM" => $"Form {result.Data} không còn hợp lệ, sửa và publish lại.",
                    "ROUTE_CONFLICT" => $"Trùng route: {result.Data}",
                    _ => Describe(result.Code)
                };
            }
            return Redirect(Self);
        }

        public async Task<IActionResult> OnPostDownloadAsync(int packageId)
        {
            OpResult result = await forms.DownloadPackageAsync(Identity!, Project, packageId, Aborted);
            if (!result.Success)
            {
                FlashError = Describe(result.Code);
                return Redirect(Self);
            }
            PackageFile file = (PackageFile)result.Data!;
            return File(file.Bytes, "application/zip", file.FileName);
        }

        public async Task<IActionResult> OnPostApplyAsync(int packageId, bool apply)
        {
            OpResult result = await forms.ApplyToFolderAsync(Identity!, Project, packageId, apply, Aborted);
            if (!Report(result, apply ? "Đã áp gói lên folder dự án (WebApi tự nạp lại form)." : "Kiểm thử xong, chưa ghi gì."))
            {
                return Redirect(Self);
            }
            Applied = (ApplyResult)result.Data!;
            await LoadAsync();
            return Page();
        }

        [RequestSizeLimit(128 * 1024)]
        public async Task<IActionResult> OnPostImportAsync(IFormFile? result)
        {
            string? json = null;
            if (result is { Length: > 0 and <= FormManagementService.MaxResultBytes })
            {
                using StreamReader reader = new(result.OpenReadStream(), Encoding.UTF8);
                json = await reader.ReadToEndAsync(Aborted);
            }
            OpResult recorded = await forms.RecordApplyResultAsync(Identity!, Project, json, Aborted);
            Report(recorded, $"Đã ghi nhận {recorded.Data}.");
            return Redirect(Self);
        }

        private async Task<bool> LoadAsync()
        {
            OpResult packages = await forms.ListPackagesAsync(Identity!, Project, Aborted);
            if (!packages.Success)
            {
                return false;
            }
            Packages = (IReadOnlyList<PackageInfo>)packages.Data!;
            OpResult log = await forms.ListSyncLogAsync(Identity!, Project, 1, Aborted);
            SyncLog = log.Success ? ((SyncLogPage)log.Data!).Items : [];
            return true;
        }
    }
}
