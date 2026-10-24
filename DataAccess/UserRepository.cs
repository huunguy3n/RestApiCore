using Business;
using Microsoft.Data.SqlClient;
using Models;
using Provider;
using System.Data;

namespace DataAccess
{
    public class UserRepository(SqlDatabase database) : IUserRightsRepository, IPortalCredentialStore
    {
        /// <summary>
        /// Portal v3: restApiUser.password (AES, khóa passwordKey) của tài khoản còn hiệu lực; null nếu không có.
        /// Không gộp vào restApi_GetUserRights để mật khẩu không nằm trong cache quyền
        /// </summary>
        public async Task<string?> GetEncryptedPasswordAsync(string name, CancellationToken cancellationToken = default)
        {
            var ds = await database.RetrieveAsync("SELECT [password] FROM restApiUser WHERE [name] = @name AND status = '1'", cancellationToken,
                new SqlParameter("@name", SqlDbType.VarChar, 32) { Value = name });
            return ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0 ? ds.Tables[0].Rows[0]["password"] as string : null;
        }

        /// <summary>
        /// v3: một lượt gọi restApi_GetUserRights, tra restApiUser → name_erp → userinfo2.
        /// Trả null nếu không có tài khoản; còn hiệu lực hay không xem UserRights.IsActive
        /// </summary>
        public async Task<UserRights?> GetUserRightsAsync(string name, CancellationToken cancellationToken = default)
        {
            var ds = await database.RetrieveAsync("exec restApi_GetUserRights @UserName", cancellationToken,
                new SqlParameter("@UserName", SqlDbType.VarChar, 32) { Value = name });

            if (ds.Tables.Count < 4 || ds.Tables[0].Rows.Count == 0)
            {
                return null;
            }

            DataRow row = ds.Tables[0].Rows[0];
            UserRights rights = new()
            {
                UId = Convert.ToInt32(row["u_id"]),
                Name = row["name"].ToString()!.TrimEnd(),
                NameErp = row["name_erp"].ToString()!.TrimEnd(),
                Status = FirstChar(row["status"]),
                PrivateKey = row["private_key"] as string,
                UserId = row["user_id"] as int?,
                ErpAdmin = row["admin"] != DBNull.Value && Convert.ToInt32(row["admin"]) == 1,
                ErpStatus = FirstChar(row["erp_status"])
            };

            foreach (DataRow form in ds.Tables[1].Rows)
            {
                rights.AllowForms.Add(form["form"].ToString()!.Trim());
            }
            rights.AllowIps.AddRange(ds.Tables[2].Rows.Cast<DataRow>().Select(o => o["ip_address"].ToString()!.Trim()));
            rights.BlackListIps.AddRange(ds.Tables[3].Rows.Cast<DataRow>().Select(o => o["ip_address"].ToString()!.Trim()));

            return rights;
        }

        private static char FirstChar(object value)
        {
            string text = value == DBNull.Value ? "" : value.ToString()!.Trim();
            return text.Length > 0 ? text[0] : '0';
        }
    }
}
