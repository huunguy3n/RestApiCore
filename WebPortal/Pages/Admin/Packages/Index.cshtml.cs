using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;
using Models;

namespace WebPortal.Pages.Admin.Packages
{
    public class IndexModel(FormManagementService forms, ProjectAccessService access) : AdminPageModel
    {
        public IReadOnlyList<ProjectInfo> Projects { get; private set; } = [];
        public int? ProjectId { get; private set; }
        public IReadOnlyList<PackageInfo>? Packages { get; private set; }
        public bool CanPublish { get; private set; }

        public Task<IActionResult> OnGetAsync(int? project) => RunAsync(async () =>
        {
            Projects = await ProjectsHelper.StaffProjectsAsync(access, AccountId, HttpContext.RequestAborted);
            ProjectId = project ?? Projects.FirstOrDefault()?.Id;
            if (ProjectId is int id)
            {
                OpResult result = await forms.ListPackagesAsync(AccountId, id, HttpContext.RequestAborted);
                if (result.Success)
                {
                    Packages = (IReadOnlyList<PackageInfo>)result.Data!;
                    AccountAccess? actor = await access.GetAccessAsync(AccountId, HttpContext.RequestAborted);
                    CanPublish = actor?.Can(id, PortalPermissions.FormsPublish) == true;
                }
                else
                {
                    FlashError = Describe(result.Code);
                }
            }
            return Page();
        });

        public Task<IActionResult> OnPostCreateAsync(int project) => RunAsync(async () =>
        {
            OpResult result = await forms.CreatePackageAsync(AccountId, project, HttpContext.RequestAborted);
            if (result.Success)
            {
                Flash = $"Đã tạo gói #{result.Data}.";
            }
            else
            {
                FlashError = result.Code switch
                {
                    "NO_FORMS" => "Dự án chưa có form nào được publish.",
                    "INVALID_FORM" => $"Form {result.Data} không còn hợp lệ theo luật hiện tại: mở, sửa và publish lại.",
                    "ROUTE_CONFLICT" => $"Trùng route giữa các form: {result.Data}",
                    _ => Describe(result.Code)
                };
            }
            return RedirectToPage(new { project });
        });

        /// <summary>Tải file zip: tải xuống dưới dạng đính kèm, không bao giờ hiển thị trực tiếp</summary>
        public Task<IActionResult> OnGetDownloadAsync(int project, int id) => RunAsync(async () =>
        {
            OpResult result = await forms.DownloadPackageAsync(AccountId, project, id, HttpContext.RequestAborted);
            if (!result.Success)
            {
                FlashError = result.Code == "INTEGRITY" ? "Gói không qua được kiểm toàn vẹn, không giao file." : Describe(result.Code);
                return RedirectToPage(new { project });
            }
            PackageFile file = (PackageFile)result.Data!;
            return File(file.Bytes, "application/zip", file.FileName);
        });

        private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }
    }
}
