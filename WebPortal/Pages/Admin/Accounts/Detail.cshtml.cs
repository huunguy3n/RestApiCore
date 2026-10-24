using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;

namespace WebPortal.Pages.Admin.Accounts
{
    public class DetailModel(AccountAdminService accounts) : AdminPageModel
    {
        public AccountDetail? Detail { get; private set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            try
            {
                return await LoadAsync(id);
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }

        public Task<IActionResult> OnPostSetActiveAsync(int id, bool active) =>
            RunAsync(id, async () => Report(await accounts.SetActiveAsync(AccountId, id, active, HttpContext.RequestAborted), active ? "Đã mở khóa tài khoản." : "Đã khóa tài khoản."));

        public Task<IActionResult> OnPostAddMembershipAsync(int id, int? projectId, string? roleCode, string? projectUser) =>
            RunAsync(id, async () => Report(await accounts.AddMembershipAsync(AccountId, id, projectId, roleCode, projectUser, HttpContext.RequestAborted), "Đã thêm phân quyền."));

        public Task<IActionResult> OnPostRemoveMembershipAsync(int id, int membershipId) =>
            RunAsync(id, async () => Report(await accounts.RemoveMembershipAsync(AccountId, membershipId, HttpContext.RequestAborted), "Đã gỡ phân quyền."));

        private async Task<IActionResult> RunAsync(int id, Func<Task<bool>> action)
        {
            try
            {
                await action();
                return RedirectToPage(new { id });
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }

        private async Task<IActionResult> LoadAsync(int id)
        {
            OpResult result = await accounts.GetDetailAsync(AccountId, id, HttpContext.RequestAborted);
            if (!result.Success)
            {
                return NotFound();
            }
            Detail = (AccountDetail)result.Data!;
            return Page();
        }
    }
}
