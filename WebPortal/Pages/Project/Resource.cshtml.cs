using System.Text.Json;
using Business;
using Microsoft.AspNetCore.Mvc;
using Models;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    /// <summary>Chi tiết một form được cấp: khối, field, mẫu body, gọi thử (ký HMAC ngay trên trình duyệt)</summary>
    public class ResourceModel(ApiDocsService docs) : ProjectPageModel
    {
        public override string NavKey => "overview";

        [BindProperty(SupportsGet = true, Name = "form")] public string? FormName { get; set; }

        public ImportForm? Form { get; private set; }
        public IReadOnlyList<BlockDescription> Blocks { get; private set; } = [];
        public string SampleJson { get; private set; } = "";

        public async Task<IActionResult> OnGetAsync()
        {
            OpResult result = await docs.FormAsync(Identity!, Project, FormName, Aborted);
            if (result.Code is "FORBIDDEN")
            {
                return Denied();
            }
            if (!result.Success)
            {
                return NotFound();
            }
            Form = (ImportForm)result.Data!;
            Blocks = FormDescriber.Blocks(Form);
            SampleJson = JsonSerializer.Serialize(FormDescriber.Sample(Form), new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            return Page();
        }
    }
}
