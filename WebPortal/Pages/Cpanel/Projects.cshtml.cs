using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Cpanel
{
    public class ProjectsModel(ProjectService projects) : CpanelPageModel
    {
        public override string NavKey => "projects";

        public IReadOnlyList<ProjectView> Projects { get; private set; } = [];
        public IReadOnlyList<QldaProject> Candidates { get; private set; } = [];
        public string? QldaError { get; private set; }

        public async Task OnGetAsync() => await LoadAsync();

        public async Task<IActionResult> OnPostAsync(string? maDa, string? apiFolder, string? apiBaseUrl)
        {
            OpResult result;
            try
            {
                result = await projects.CreateAsync(Identity!, maDa, apiFolder, apiBaseUrl, ClientIp, Aborted);
            }
            catch (InvalidOperationException ex)
            {
                result = new OpResult("QLDA_INACTIVE", ex.Message);
            }
            if (Report(result, $"Đã thêm dự án {maDa}."))
            {
                return Redirect("/cpanel/projects");
            }
            await LoadAsync();
            return Page();
        }

        private async Task LoadAsync()
        {
            Projects = await projects.ListAsync(Identity!, Aborted);
            try
            {
                Candidates = await projects.CandidatesAsync(Aborted);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Provider.DatabaseException or Microsoft.Data.SqlClient.SqlException)
            {
                QldaError = "Không đọc được QLDA (Portal:Qlda): không thêm được dự án mới lúc này.";
            }
        }
    }
}
