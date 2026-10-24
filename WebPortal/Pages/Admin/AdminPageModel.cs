using System.Security.Claims;
using WebPortal.ControlPlane;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebPortal.Pages.Admin
{
    /// <summary>Nền chung cho trang /Admin: id tài khoản đang đăng nhập, thông báo qua TempData, dịch mã lỗi</summary>
    [RequestSizeLimit(1_048_576)]
    public abstract class AdminPageModel : PageModel
    {
        protected int AccountId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;

        [TempData] public string? Flash { get; set; }
        [TempData] public string? FlashError { get; set; }

        public static string Describe(string code) => code switch
        {
            "FORBIDDEN" => "Bạn không có quyền thực hiện thao tác này.",
            "NOT_FOUND" => "Không tìm thấy đối tượng.",
            "INVALID_INPUT" => "Dữ liệu chưa hợp lệ (kiểm tra độ dài, ký tự cho phép; mật khẩu tối thiểu 12 ký tự).",
            "INVALID_MEMBERSHIP" => "Vai trò hoặc dự án không phù hợp với loại tài khoản.",
            "DUPLICATE" => "Đã tồn tại (tên đăng nhập, mã dự án hoặc phân quyền trùng).",
            "SELF_LOCK" => "Không thể tự khóa chính mình.",
            "LAST_ADMIN" => "Không thể khóa hoặc gỡ administrator trung tâm cuối cùng.",
            "NAME_CASE_CONFLICT" => "Đã có form cùng tên khác chữ hoa/thường (trên Windows là cùng một file).",
            "TOO_MANY_ATTEMPTS" => "Nhập sai quá nhiều lần. Thử lại sau ít phút.",
            "INVALID_CREDENTIAL" => "Mật khẩu hiện tại không đúng.",
            _ => "Không thực hiện được."
        };

        /// <summary>Ghi kết quả vào thông báo; trả true nếu thành công</summary>
        protected bool Report(OpResult result, string success)
        {
            if (result.Success)
            {
                Flash = success;
                return true;
            }
            FlashError = Describe(result.Code);
            return false;
        }

        /// <summary>Kho chưa cấu hình: báo thay vì lỗi 500</summary>
        protected IActionResult NotConfigured()
        {
            FlashError = "Portal chưa được cấu hình (ControlPlane:ConnectionString).";
            return Page();
        }
    }
}
