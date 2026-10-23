using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApiDocs.Pages
{
    public class IndexModel(IConfiguration configuration) : PageModel
    {
        /// <summary>Địa chỉ Api (Portal:ApiBaseUrl); rỗng là cùng origin với portal</summary>
        public string ApiBaseUrl { get; } = (configuration["Portal:ApiBaseUrl"] ?? "").Trim().TrimEnd('/');

        public void OnGet()
        {
        }
    }
}
