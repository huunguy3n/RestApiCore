using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;

namespace WebPortal.Pages.Admin.Projects
{
    public class IndexModel(AccountAdminService accounts) : AdminPageModel
    {
        public IReadOnlyList<ProjectDetail>? Projects { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                OpResult result = await accounts.ListProjectsAsync(AccountId, HttpContext.RequestAborted);
                if (!result.Success)
                {
                    FlashError = Describe(result.Code);
                    return Page();
                }
                Projects = (IReadOnlyList<ProjectDetail>)result.Data!;
                return Page();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }

        public Task<IActionResult> OnPostCreateAsync(string? code, string? name, string? apiBaseUrl) =>
            RunAsync(async () => Report(await accounts.CreateProjectAsync(AccountId, code, name, apiBaseUrl, HttpContext.RequestAborted), "Đã thêm dự án."));

        /// <summary>Ô đánh dấu không gửi giá trị khi bỏ chọn: active=false nghĩa là ngừng dự án (tham số vắng mặt)</summary>
        public Task<IActionResult> OnPostUpdateAsync(int id, string? name, string? apiBaseUrl, bool active = false) =>
            RunAsync(async () => Report(await accounts.UpdateProjectAsync(AccountId, id, name, apiBaseUrl, active, HttpContext.RequestAborted), "Đã lưu dự án."));

        private async Task<IActionResult> RunAsync(Func<Task<bool>> action)
        {
            try
            {
                await action();
                return RedirectToPage();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }
    }
}
