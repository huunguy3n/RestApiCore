using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.RegularExpressions;
using AjaxSession = FastBusiness.AjaxControlExtender.Session;

namespace WebProject.ApiLog
{
    /// <summary>Người dùng FBO đang đăng nhập (session Fast Ajax) và DB app của entity đang chọn</summary>
    public sealed class FboSession
    {
        public bool IsLogin { get; private set; }
        public int UserId { get; private set; }
        public string UserName { get; private set; }
        public bool IsAdmin { get; private set; }
        public string AppDatabaseName { get; private set; }

        public static FboSession Read()
        {
            int userId;
            int.TryParse(Text(AjaxSession.SessionName.userID), NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);
            return new FboSession
            {
                IsLogin = IsTrue(Text(AjaxSession.SessionName.isLogin)),
                UserId = userId,
                UserName = Text(AjaxSession.SessionName.userName),
                IsAdmin = IsTrue(Text(AjaxSession.SessionName.admin)),
                AppDatabaseName = Text(AjaxSession.SessionName.appDatabaseName)
            };
        }

        /// <summary>null = được phép; UNAUTHORIZED (chưa đăng nhập) hoặc FORBIDDEN (không có quyền menu)</summary>
        public string Authorize(string menuSysId)
        {
            if (!IsLogin || UserId <= 0) return "UNAUTHORIZED";
            if (IsAdmin) return null;
            ConnectionStringSettings sys = ConfigurationManager.ConnectionStrings["sysConnectionString"];
            if (sys == null) return "FORBIDDEN";
            DataSet rights = global::Provider.Provider.Retrieve(sys.ConnectionString,
                "SELECT dbo.[FastBusiness$System$CheckAccessRight](@id, @c) AS ok",
                new SqlParameter("@id", SqlDbType.Int) { Value = UserId },
                new SqlParameter("@c", SqlDbType.VarChar, 128) { Value = menuSysId });
            return Convert.ToInt32(rights.Tables[0].Rows[0]["ok"], CultureInfo.InvariantCulture) == 1 ? null : "FORBIDDEN";
        }

        /// <summary>appConnectionString có Initial Catalog=%Database: thay bằng DB của entity đang chọn (chỉ nhận tên an toàn)</summary>
        public string AppConnectionString()
        {
            ConnectionStringSettings app = ConfigurationManager.ConnectionStrings["appConnectionString"];
            if (app == null || !Regex.IsMatch(AppDatabaseName ?? "", "^[A-Za-z0-9_]{1,128}$")) return null;
            return app.ConnectionString.Replace("%Database", AppDatabaseName);
        }

        private static bool IsTrue(string text)
        {
            return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string Text(AjaxSession.SessionName name)
        {
            try
            {
                object value = AjaxSession.GetSession(name);
                return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            }
            catch (NullReferenceException)
            {
                // Không có session (gọi ngoài FBO hoặc session hết hạn)
                return "";
            }
        }
    }
}
