using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApiDocs.Pages.Api
{
    public class ListModel : PageModel
    {
        public List<ApiInfo> ApiList { get; set; } = new();

        public void OnGet()
        {
            // Dữ liệu fake, sau sẽ lấy từ file JSON hoặc DB
            ApiList = new List<ApiInfo>
            {
                new ApiInfo { Name = "Get Users", Method = "GET", Route = "/api/users", Group = "User", Description = "Lấy danh sách user" },
                new ApiInfo { Name = "Create User", Method = "POST", Route = "/api/users", Group = "User", Description = "Tạo user mới" },
                new ApiInfo { Name = "Get Orders", Method = "GET", Route = "/api/orders", Group = "Order", Description = "Lấy đơn hàng" }
            };
        }

        public class ApiInfo
        {
            public string Name { get; set; }
            public string Method { get; set; }
            public string Route { get; set; }
            public string Group { get; set; }
            public string Description { get; set; }
        }
    }
}
