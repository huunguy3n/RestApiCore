using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class FormsModel(FormManagementService forms, IProjectConnectionResolver resolver) : ProjectPageModel
    {
        public override string NavKey => "forms";

        public IReadOnlyList<FormSummary> Forms { get; private set; } = [];
        public IReadOnlyList<string> FolderOnly { get; private set; } = [];

        public async Task<IActionResult> OnGetAsync()
        {
            OpResult result = await forms.ListFormsAsync(Identity!, Project, Aborted);
            if (!result.Success)
            {
                return Denied();
            }
            Forms = ((FormListView)result.Data!).Forms;
            try
            {
                string directory = resolver.Resolve(Project).FormDirectory;
                HashSet<string> known = new(Forms.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                FolderOnly = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "*.xml").Select(Path.GetFileNameWithoutExtension).Where(n => n != null && !known.Contains(n)).Order().ToList()!
                    : [];
            }
            catch (Exception ex) when (ApiUserService.IsProjectError(ex))
            {
                FlashError = ApiUserService.ProjectMessage(ex);
            }
            return Page();
        }
    }
}
