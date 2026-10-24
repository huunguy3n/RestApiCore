using ApiDocs.ControlPlane;
using Business;
using Microsoft.AspNetCore.Mvc;

namespace ApiDocs.Pages.Admin.Forms
{
    public class EditModel(FormManagementService forms) : AdminPageModel
    {
        [BindProperty(SupportsGet = true)] public int Project { get; set; }
        [BindProperty(SupportsGet = true)] public string? Form { get; set; }

        public FormEditView? View { get; private set; }
        public string Content { get; private set; } = "";
        public int? BaseRevision { get; private set; }
        public FormValidation? Validation { get; private set; }

        public Task<IActionResult> OnGetAsync(int? revision) => RunAsync(async () =>
        {
            await LoadAsync(revision);
            return Page();
        });

        /// <summary>Kiểm tra không lưu: giữ nguyên nội dung người dùng đang gõ</summary>
        public Task<IActionResult> OnPostValidateAsync(string? content, int? baseRevision) => RunAsync(async () =>
        {
            OpResult result = await forms.ValidateAsync(AccountId, Project, Form ?? "", content, HttpContext.RequestAborted);
            await LoadAsync(null);
            Content = content ?? "";
            BaseRevision = baseRevision;
            if (result.Success)
            {
                Validation = (FormValidation)result.Data!;
            }
            else
            {
                FlashError = Describe(result.Code);
            }
            return Page();
        });

        public Task<IActionResult> OnPostSaveAsync(string? content, int? baseRevision) => RunAsync(async () =>
        {
            OpResult result = await forms.SaveAsync(AccountId, Project, Form ?? "", baseRevision, content, HttpContext.RequestAborted);
            if (result.Success)
            {
                SaveResult saved = (SaveResult)result.Data!;
                Flash = $"Đã lưu revision {saved.Revision} ({(saved.Status == "validated" ? "hợp lệ, có thể publish" : "còn lỗi, chưa publish được")}).";
                return RedirectToPage(new { project = Project, form = Form });
            }

            // Xung đột hoặc lỗi: giữ nguyên chữ người dùng đã gõ để không mất công, không tự ghi đè
            await LoadAsync(null);
            Content = content ?? "";
            BaseRevision = baseRevision;
            FlashError = result.Code == "CONFLICT"
                ? $"Có người vừa lưu revision {result.Data} trong lúc bạn sửa. Mở bản mới nhất, so sánh rồi gộp thay đổi của bạn vào (nội dung bạn gõ vẫn còn trong khung soạn)."
                : Describe(result.Code);
            return Page();
        });

        public Task<IActionResult> OnPostPublishAsync(int revision) => RunAsync(async () =>
        {
            OpResult result = await forms.PublishAsync(AccountId, Project, Form ?? "", revision, HttpContext.RequestAborted);
            if (result.Success)
            {
                Flash = $"Đã publish revision {revision}.";
            }
            else
            {
                FlashError = result.Code switch
                {
                    "INVALID_STATE" => "Chỉ revision đã kiểm tra hợp lệ (validated) mới publish được.",
                    "INVALID_FORM" => "Revision không còn hợp lệ theo luật hiện tại, hãy mở lại, sửa và lưu revision mới.",
                    "ROUTE_CONFLICT" => $"Trùng route với form đang hiệu lực khác: {result.Data}",
                    _ => Describe(result.Code)
                };
            }
            return RedirectToPage(new { project = Project, form = Form });
        });

        private async Task LoadAsync(int? revision)
        {
            OpResult result = await forms.GetFormAsync(AccountId, Project, Form ?? "", revision, HttpContext.RequestAborted);
            if (!result.Success)
            {
                FlashError = Describe(result.Code);
                return;
            }
            View = (FormEditView)result.Data!;
            Content = View.Current?.Content ?? FormManagementService.NewFormTemplate;
            BaseRevision = View.LatestRevision;
            Validation = View.Validation;
        }

        private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }
    }
}
