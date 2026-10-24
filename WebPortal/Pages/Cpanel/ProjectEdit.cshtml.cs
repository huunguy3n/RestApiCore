using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class ProjectEditModel(IPortalStore store, ProjectService projects, AccountService accounts) : CpanelPageModel
    {
        public override string NavKey => "projects";

        [BindProperty(SupportsGet = true)] public int Id { get; set; }

        public PortalProject? Project { get; private set; }
        public IReadOnlyList<PortalAccount> ProjectAdmins { get; private set; } = [];
        public IReadOnlyList<PortalAccount> OtherAdmins { get; private set; } = [];

        public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

        public async Task<IActionResult> OnPostSaveAsync(string? apiFolder, string? apiBaseUrl, bool active)
        {
            Report(await projects.UpdateAsync(Identity!, Id, apiFolder, apiBaseUrl, active, ClientIp, Aborted), "Đã lưu.");
            return Redirect($"/cpanel/projects/{Id}");
        }

        public async Task<IActionResult> OnPostCheckAsync()
        {
            PortalProject? project = await store.FindProjectAsync(Id, Aborted);
            if (project == null)
            {
                return NotFound();
            }
            FolderCheck check = await projects.CheckAsync(project, Aborted);
            if (check.Ok)
            {
                Flash = check.Message;
            }
            else
            {
                FlashError = check.Message;
            }
            return Redirect($"/cpanel/projects/{Id}");
        }

        public async Task<IActionResult> OnPostAssignAsync(int adminId)
        {
            Report(await accounts.SetProjectAsync(Identity!, adminId, Id, true, ClientIp, Aborted), "Đã gán Admin.");
            return Redirect($"/cpanel/projects/{Id}");
        }

        public async Task<IActionResult> OnPostRemoveAsync(int adminId)
        {
            Report(await accounts.SetProjectAsync(Identity!, adminId, Id, false, ClientIp, Aborted), "Đã gỡ Admin khỏi dự án.");
            return Redirect($"/cpanel/projects/{Id}");
        }

        private async Task<bool> LoadAsync()
        {
            Project = await store.FindProjectAsync(Id, Aborted);
            if (Project == null)
            {
                return false;
            }
            ProjectAdmins = await store.GetProjectAdminsAsync(Id, Aborted);
            HashSet<int> assigned = [.. ProjectAdmins.Select(a => a.Id)];
            OtherAdmins = (await store.ListAdminsAsync(Aborted)).Where(a => a.Active && !assigned.Contains(a.Id)).ToList();
            return true;
        }
    }
}
