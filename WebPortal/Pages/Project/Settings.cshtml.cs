using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class SettingsModel(ProjectService projects, ProjectSyncService sync, IQldaDirectory qlda, IPortalStore store) : ProjectPageModel
    {
        public override string NavKey => "settings";

        public string SuggestedFolder { get; private set; } = "";
        public IReadOnlyList<DriftItem>? Drift { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!CanAdmin)
            {
                return Denied();
            }
            await LoadSuggestionAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostSaveAsync(string? apiFolder, string? apiBaseUrl)
        {
            Report(await projects.UpdateAsync(Identity!, Project.Id, apiFolder, apiBaseUrl, null, ClientIp, Aborted), "Đã lưu.");
            return Redirect($"/{MaDa}/settings");
        }

        public async Task<IActionResult> OnPostCheckAsync()
        {
            if (!CanAdmin)
            {
                return Denied();
            }
            FolderCheck check = await projects.CheckAsync(Project, Aborted);
            if (check.Ok)
            {
                Flash = check.Message;
            }
            else
            {
                FlashError = check.Message;
            }
            return Redirect($"/{MaDa}/settings");
        }

        public async Task<IActionResult> OnPostDriftAsync()
        {
            if (!CanAdmin)
            {
                return Denied();
            }
            try
            {
                Drift = await sync.DriftAsync(Project, Aborted);
            }
            catch (Exception ex) when (ApiUserService.IsProjectError(ex))
            {
                FlashError = ApiUserService.ProjectMessage(ex);
            }
            await LoadSuggestionAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostSyncAllAsync()
        {
            if (!CanAdmin)
            {
                return Denied();
            }
            int failed = await sync.PushAllAsync(Project, Aborted);
            await store.WriteAuditAsync(PortalGuard.Audit(Identity, Project.Id, "apiuser.sync.all", Project.MaDa, failed == 0 ? "SUCCESS" : "FAILED", $"{failed} user lỗi", ClientIp), Aborted);
            if (failed == 0)
            {
                Flash = "Đã đồng bộ mọi user xuống dự án.";
            }
            else
            {
                FlashError = $"{failed} user chưa đồng bộ được (xem cột Đồng bộ ở danh sách user).";
            }
            return Redirect($"/{MaDa}/settings");
        }

        private async Task LoadSuggestionAsync()
        {
            try
            {
                SuggestedFolder = ProjectService.SuggestApiFolder((await qlda.FindAsync(MaDa, Aborted))?.SourceDir);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException)
            {
                SuggestedFolder = "";
            }
        }
    }
}
