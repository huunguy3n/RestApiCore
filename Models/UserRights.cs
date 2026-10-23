using System.Text.RegularExpressions;

namespace Models
{
    /// <summary>
    /// Kết quả restApi_GetUserRights: tài khoản API v3 nối sang user FBO qua restApiUser.name_erp = userinfo2.name.
    /// UId (restApiUser.id) và UserId (userinfo2.id) là hai định danh độc lập
    /// </summary>
    public class UserRights
    {
        /// <summary>restApiUser.id (= u_id trong các bảng restApi*)</summary>
        public int UId { get; set; }
        /// <summary>restApiUser.name, tên đăng nhập API</summary>
        public string Name { get; set; } = string.Empty;
        public string NameErp { get; set; } = string.Empty;
        public char Status { get; set; } = '0';
        /// <summary>Khóa HMAC đã mã hóa, null = chưa cấp</summary>
        public string? PrivateKey { get; set; }

        /// <summary>userinfo2.id, dùng cho mọi kiểm quyền FBO. Null = name_erp không khớp user FBO nào</summary>
        public int? UserId { get; set; }
        public bool ErpAdmin { get; set; }
        public char ErpStatus { get; set; } = '0';

        public HashSet<string> AllowForms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> AllowIps { get; set; } = new();
        public List<string> BlackListIps { get; set; } = new();

        /// <summary>
        /// Còn hiệu lực khi cả tài khoản API lẫn user FBO đều status = '1'
        /// </summary>
        public bool IsActive => Status == '1' && UserId != null && ErpStatus == '1';

        /// <summary>
        /// v3 mặc định từ chối: không có dòng restApiAllowForm nào thì không có quyền form nào
        /// </summary>
        public bool CanAccessForm(string form) => AllowForms.Contains(form);

        /// <summary>
        /// Như restApi_CheckIPAddress: IP bị chặn thì từ chối; user không khai IP thì cho mọi IP; "255" là wildcard
        /// </summary>
        public bool IsIpAllowed(string ip)
        {
            if (BlackListIps.Any(pattern => MatchIp(ip, pattern)))
            {
                return false;
            }
            return AllowIps.Count == 0 || AllowIps.Any(pattern => MatchIp(ip, pattern));
        }

        /// <summary>
        /// Tương đương "@ip LIKE REPLACE(ip_address, '255', '%')" của SQL (không phân biệt hoa thường)
        /// </summary>
        public static bool MatchIp(string ip, string pattern)
        {
            string regex = "^" + string.Join(".*", pattern.Trim().Split("255").Select(Regex.Escape)) + "$";
            return Regex.IsMatch(ip, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
