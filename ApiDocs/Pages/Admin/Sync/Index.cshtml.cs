using ApiDocs.ControlPlane;
using Microsoft.AspNetCore.Mvc;
using Models;

namespace ApiDocs.Pages.Admin.Sync
{
    [RequestSizeLimit(131_072)]
    public class IndexModel(FormManagementService forms, ProjectAccessService access) : AdminPageModel
    {
        public IReadOnlyList<ProjectInfo> Projects { get; private set; } = [];
        public IReadOnlyList<ProjectInfo> CanImportFor { get; private set; } = [];
        public SyncLogPage? Log { get; private set; }
        public int? ProjectId { get; private set; }
        public string? Env { get; private set; }
        public string? Outcome { get; private set; }
        public int PageNumber { get; private set; } = 1;

        public Task<IActionResult> OnGetAsync(int? projectId, string? env, string? outcome, int pageNumber = 1) => RunAsync(async () =>
        {
            (ProjectId, Env, Outcome, PageNumber) = (projectId, env, outcome, Math.Max(pageNumber, 1));
            Projects = await ProjectsHelper.StaffProjectsAsync(access, AccountId, HttpContext.RequestAborted);
            AccountAccess? actor = await access.GetAccessAsync(AccountId, HttpContext.RequestAborted);
            CanImportFor = Projects.Where(p => actor?.Can(p.Id, PortalPermissions.FormsPublish) == true).ToList();

            OpResult result = await forms.ListSyncLogAsync(AccountId, projectId, env, outcome, PageNumber, HttpContext.RequestAborted);
            if (result.Success)
            {
                Log = (SyncLogPage)result.Data!;
            }
            else
            {
                FlashError = Describe(result.Code);
            }
            return Page();
        });

        /// <summary>File kết quả nhỏ (tối đa 64 KB), đọc thành văn bản rồi giao cho service kiểm toàn bộ; không bao giờ lưu file lên đĩa</summary>
        public Task<IActionResult> OnPostImportAsync(int project, IFormFile? result) => RunAsync(async () =>
        {
            if (result == null || result.Length is 0 or > FormManagementService.MaxResultBytes)
            {
                FlashError = "File kết quả phải có và nhỏ hơn 64 KB.";
                return RedirectToPage();
            }

            string text;
            using (StreamReader reader = new(result.OpenReadStream()))
            {
                text = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            }

            OpResult recorded = await forms.RecordApplyResultAsync(AccountId, project, text, HttpContext.RequestAborted);
            if (recorded.Success)
            {
                Flash = $"Đã ghi nhận kết quả ({recorded.Data}).";
            }
            else
            {
                FlashError = recorded.Code switch
                {
                    "PACKAGE_MISMATCH" => "Kết quả không khớp gói đã lưu của dự án (sai gói hoặc SHA-256 manifest).",
                    "INVALID_INPUT" => "File kết quả không đúng định dạng.",
                    _ => Describe(recorded.Code)
                };
            }
            return RedirectToPage();
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
