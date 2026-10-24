using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class AdminsModel(IPortalStore store, AccountService accounts) : CpanelPageModel
    {
        public override string NavKey => "admins";

        public IReadOnlyList<AdminView> Admins { get; private set; } = [];
        public IReadOnlyList<PortalProject> Projects { get; private set; } = [];

        public async Task OnGetAsync() => await LoadAsync();

        public async Task<IActionResult> OnPostAsync(string? userName, string? displayName, string? password, int[]? projectIds)
        {
            if (Report(await accounts.CreateAdminAsync(Identity!, userName, displayName, password, projectIds ?? [], ClientIp, Aborted), $"Đã tạo Admin {userName}."))
            {
                return Redirect("/cpanel/admins");
            }
            await LoadAsync();
            return Page();
        }

        private async Task LoadAsync()
        {
            OpResult result = await accounts.ListAdminsAsync(Identity!, Aborted);
            Admins = result.Success ? (IReadOnlyList<AdminView>)result.Data! : [];
            Projects = (await store.ListProjectsAsync(Aborted)).Where(p => p.Active).ToList();
        }
    }
}
