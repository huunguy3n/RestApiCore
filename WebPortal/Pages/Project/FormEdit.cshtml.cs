using Business;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages.Project
{
    public class FormEditModel(FormManagementService forms) : ProjectPageModel
    {
        public override string NavKey => "forms";

        [BindProperty(SupportsGet = true)] public string? Form { get; set; }

        public FormEditView? View { get; private set; }
        public string Content { get; private set; } = "";
        public int? BaseRevision { get; private set; }
        public FormValidation? Validation { get; private set; }

        private string Self => $"/{MaDa}/forms/{Form}";

        public async Task<IActionResult> OnGetAsync(int? revision, string? name)
        {
            // /forms/new?name=xxx: soạn form mới
            if (string.Equals(Form, "new", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(name))
            {
                return Redirect($"/{MaDa}/forms/{Uri.EscapeDataString(name.Trim())}");
            }
            return await LoadAsync(revision) ? Page() : Denied();
        }

        /// <summary>Kiểm tra không lưu: giữ nguyên nội dung người dùng đang gõ</summary>
        public async Task<IActionResult> OnPostValidateAsync(string? content, int? baseRevision)
        {
            OpResult result = await forms.ValidateAsync(Identity!, Project, Form ?? "", content, Aborted);
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
        }

        public async Task<IActionResult> OnPostSaveAsync(string? content, int? baseRevision)
        {
            OpResult result = await forms.SaveAsync(Identity!, Project, Form ?? "", baseRevision, content, Aborted);
            if (result.Success)
            {
                SaveResult saved = (SaveResult)result.Data!;
                Flash = $"Đã lưu revision {saved.Revision} ({(saved.Status == "validated" ? "hợp lệ, có thể publish" : "còn lỗi, chưa publish được")}).";
                return Redirect(Self);
            }
            // Xung đột hoặc lỗi: giữ nguyên chữ người dùng đã gõ, không tự ghi đè
            await LoadAsync(null);
            Content = content ?? "";
            BaseRevision = baseRevision;
            FlashError = result.Code == "CONFLICT"
                ? $"Có người vừa lưu revision {result.Data} trong lúc bạn sửa. Mở bản mới nhất, so sánh rồi gộp thay đổi của bạn vào (nội dung bạn gõ vẫn còn trong khung soạn)."
                : Describe(result.Code);
            return Page();
        }

        public async Task<IActionResult> OnPostPublishAsync(int revision)
        {
            OpResult result = await forms.PublishAsync(Identity!, Project, Form ?? "", revision, Aborted);
            if (result.Success)
            {
                Flash = $"Đã publish revision {revision}. Đóng gói ở mục Gói DEV→PROD để áp.";
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
            return Redirect(Self);
        }

        private async Task<bool> LoadAsync(int? revision)
        {
            OpResult result = await forms.GetFormAsync(Identity!, Project, Form ?? "", revision, Aborted);
            if (result.Code == "FORBIDDEN")
            {
                return false;
            }
            if (!result.Success)
            {
                FlashError = Describe(result.Code);
                return true;
            }
            View = (FormEditView)result.Data!;
            Content = View.Current?.Content ?? FormManagementService.NewFormTemplate;
            BaseRevision = View.LatestRevision;
            Validation = View.Validation;
            return true;
        }
    }
}
