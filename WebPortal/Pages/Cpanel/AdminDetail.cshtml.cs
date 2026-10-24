using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class AdminDetailModel(IPortalStore store, AccountService accounts) : CpanelPageModel
    {
        public override string NavKey => "admins";

        [BindProperty(SupportsGet = true)] public int Id { get; set; }

        public PortalAccount? Account { get; private set; }
        public IReadOnlyList<PortalProject> Projects { get; private set; } = [];
        public HashSet<int> Assigned { get; private set; } = [];

        public async Task<IActionResult> OnGetAsync()
        {
            Account = await store.FindAccountAsync(Id, Aborted);
            if (Account is not { Kind: AccountKind.Admin })
            {
                return NotFound();
            }
            Projects = await store.ListProjectsAsync(Aborted);
            Assigned = [.. await store.GetAdminProjectIdsAsync(Id, Aborted)];
            return Page();
        }

        public async Task<IActionResult> OnPostProjectAsync(int projectId, bool assign)
        {
            Report(await accounts.SetProjectAsync(Identity!, Id, projectId, assign, ClientIp, Aborted), assign ? "Đã gán dự án." : "Đã gỡ dự án.");
            return Redirect($"/cpanel/admins/{Id}");
        }

        public async Task<IActionResult> OnPostActiveAsync(bool active)
        {
            Report(await accounts.SetActiveAsync(Identity!, Id, active, ClientIp, Aborted), active ? "Đã mở khóa." : "Đã khóa và kết thúc phiên.");
            return Redirect($"/cpanel/admins/{Id}");
        }

        public async Task<IActionResult> OnPostLogoutAsync()
        {
            Report(await accounts.ForceLogoutAsync(Identity!, AccountKind.Admin, Id, ClientIp, Aborted), "Đã kết thúc phiên.");
            return Redirect($"/cpanel/admins/{Id}");
        }

        public async Task<IActionResult> OnPostPasswordAsync(string? password)
        {
            Report(await accounts.ResetPasswordAsync(Identity!, Id, password, ClientIp, Aborted), "Đã đặt lại mật khẩu, phiên cũ bị thu hồi.");
            return Redirect($"/cpanel/admins/{Id}");
        }
    }
}
