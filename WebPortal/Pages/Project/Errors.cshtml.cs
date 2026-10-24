using Microsoft.AspNetCore.Mvc;
using Models.Options;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class ErrorsModel(ApiDocsService docs) : ProjectPageModel
    {
        public override string NavKey => "errors";

        public IReadOnlyList<Message> Messages { get; private set; } = [];

        public IActionResult OnGet()
        {
            OpResult result = docs.Messages(Identity!, Project);
            if (result.Code == "FORBIDDEN")
            {
                return Denied();
            }
            if (result.Success)
            {
                Messages = (IReadOnlyList<Message>)result.Data!;
            }
            else
            {
                Report(result, "");
            }
            return Page();
        }
    }
}
