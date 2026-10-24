using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;

namespace WebPortal.Pages.Admin.Forms
{
    public class IndexModel(FormManagementService forms, ProjectAccessService access) : AdminPageModel
    {
        public IReadOnlyList<ProjectInfo> Projects { get; private set; } = [];
        public int? ProjectId { get; private set; }
        public FormListView? List { get; private set; }

        public async Task<IActionResult> OnGetAsync(int? project)
        {
            try
            {
                Projects = await ProjectsHelper.StaffProjectsAsync(access, AccountId, HttpContext.RequestAborted);
                ProjectId = project ?? Projects.FirstOrDefault()?.Id;
                if (ProjectId is int id)
                {
                    OpResult result = await forms.ListFormsAsync(AccountId, id, HttpContext.RequestAborted);
                    if (!result.Success)
                    {
                        FlashError = Describe(result.Code);
                        return Page();
                    }
                    List = (FormListView)result.Data!;
                }
                return Page();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }
    }
}
