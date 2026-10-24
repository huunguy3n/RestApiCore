namespace Models
{
    /// <summary>
    /// Quy tắc gán vai trò theo loại tài khoản portal, dùng chung cho đọc quyền (WebPortal) và tạo tài khoản (Cli):
    /// khách hàng chỉ vai trò phạm vi customer (partner) và bắt buộc gắn một dự án; staff chỉ vai trò phạm vi staff,
    /// không gắn dự án (mọi dự án) chỉ khi là administrator
    /// </summary>
    public static class PortalMembershipRules
    {
        public const string Staff = "staff";
        public const string Customer = "customer";

        public static bool IsValid(string accountKind, string roleScope, string roleCode, bool hasProject)
        {
            if (!string.Equals(roleScope, accountKind, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (accountKind == Customer)
            {
                return hasProject && roleCode == "partner";
            }
            return accountKind == Staff && (hasProject || roleCode == "administrator");
        }
    }
}
