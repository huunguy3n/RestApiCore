namespace WebPortal.ControlPlane
{
    public static class ControlPlaneConfig
    {
        /// <summary>
        /// Chuỗi kết nối DB portal. Ưu tiên khóa riêng ControlPlane:ConnectionString; không có thì dùng
        /// connectionStrings:appConnectionString (cùng quy ước với Api: khai ở appsettings.Local.json đã gitignore hoặc biến môi trường)
        /// </summary>
        public static string ConnectionString(IConfiguration configuration)
        {
            foreach (string? candidate in new[] { configuration["ControlPlane:ConnectionString"], configuration["connectionStrings:appConnectionString"] })
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate.Trim();
                }
            }
            return "";
        }
    }
}
