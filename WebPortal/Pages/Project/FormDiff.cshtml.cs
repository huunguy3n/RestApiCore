using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class FormDiffModel(FormManagementService forms) : ProjectPageModel
    {
        public override string NavKey => "forms";

        [BindProperty(SupportsGet = true)] public string? Form { get; set; }
        [BindProperty(SupportsGet = true)] public int From { get; set; }
        [BindProperty(SupportsGet = true)] public int To { get; set; }

        public IReadOnlyList<DiffLine>? Lines { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            OpResult result = await forms.DiffAsync(Identity!, Project, Form ?? "", From, To, Aborted);
            if (result.Code == "FORBIDDEN")
            {
                return Denied();
            }
            if (result.Success)
            {
                Lines = (IReadOnlyList<DiffLine>)result.Data!;
            }
            else
            {
                FlashError = Describe(result.Code);
            }
            return Page();
        }
    }
}
