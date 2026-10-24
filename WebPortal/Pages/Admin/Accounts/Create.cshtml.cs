using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;

namespace WebPortal.Pages.Admin.Accounts
{
    public class CreateModel(AccountAdminService accounts) : AdminPageModel
    {
        public sealed class CreateInput
        {
            public string? Login { get; set; }
            public string? DisplayName { get; set; }
            public string? Kind { get; set; }
            public string? RoleCode { get; set; }
            public int? ProjectId { get; set; }
            public string? ProjectUser { get; set; }
            public string? Password { get; set; }
        }

        [BindProperty] public CreateInput Input { get; set; } = new();
        public CreateOptions? Options { get; private set; }

        public async Task<IActionResult> OnGetAsync() => await LoadAsync();

        /// <summary>CSRF do Razor Pages kiểm antiforgery; quyền kiểm lại trong service, không tin các lựa chọn hiện trên form</summary>
        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                string kind = Input.Kind == AccountKind.Staff ? AccountKind.Staff : AccountKind.Customer;
                OpResult result = await accounts.CreateAccountAsync(AccountId, Input.Login, Input.DisplayName, kind, Input.Password, Input.ProjectId,
                    kind == AccountKind.Customer ? RoleCodes.Partner : Input.RoleCode, Input.ProjectUser, HttpContext.RequestAborted);
                if (result.Success)
                {
                    Flash = "Đã tạo tài khoản.";
                    return RedirectToPage("Detail", new { id = (int)result.Data! });
                }

                FlashError = Describe(result.Code);
                Input.Password = null;
                return await LoadAsync();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }

        private async Task<IActionResult> LoadAsync()
        {
            try
            {
                OpResult options = await accounts.GetCreateOptionsAsync(AccountId, HttpContext.RequestAborted);
                if (!options.Success)
                {
                    FlashError = Describe(options.Code);
                    return Page();
                }
                Options = (CreateOptions)options.Data!;
                return Page();
            }
            catch (InvalidOperationException)
            {
                return NotConfigured();
            }
        }
    }
}
