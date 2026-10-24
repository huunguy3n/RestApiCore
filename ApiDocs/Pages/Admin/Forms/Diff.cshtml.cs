using ApiDocs.ControlPlane;
using Microsoft.AspNetCore.Mvc;

namespace ApiDocs.Pages.Admin.Forms
{
    public class DiffModel(FormManagementService forms) : AdminPageModel
    {
        [BindProperty(SupportsGet = true)] public int Project { get; set; }
        [BindProperty(SupportsGet = true)] public string? Form { get; set; }
        [BindProperty(SupportsGet = true)] public int From { get; set; }
        [BindProperty(SupportsGet = true)] public int To { get; set; }

        public IReadOnlyList<DiffLine>? Lines { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                OpResult result = await forms.DiffAsync(AccountId, Project, Form ?? "", From, To, HttpContext.RequestAborted);
                if (result.Success)
                {
                    Lines = (IReadOnlyList<DiffLine>)result.Data!;
                }
                else
                {
                    FlashError = result.Code == "INVALID_INPUT" ? $"Form quá dài để so sánh (tối đa {FormManagementService.MaxDiffLines} dòng)." : Describe(result.Code);
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
