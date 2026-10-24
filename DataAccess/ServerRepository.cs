using Business;
using Microsoft.Data.SqlClient;
using Provider;

namespace DataAccess
{
    public class ServerRepository(SqlDatabase database) : IIpAccessRepository
    {
        public async Task<bool> CheckIPAddressAsync(string clientIP, string userName, CancellationToken cancellationToken = default)
        {
            var ds = await database.RetrieveAsync("exec restApi_CheckIPAddress @ip, @userName", cancellationToken,
                new SqlParameter { ParameterName = "@ip", Value = clientIP },
                new SqlParameter { ParameterName = "@userName", Value = userName }
                );

            if (ds.Tables[0].Rows.Count > 0)
            {
                return ds.Tables[0].Rows[0]["value"].ToString() == "1";
            }

            return false;
        }

        /// <summary>
        /// Engine v2 cũ: restApi_CheckFormRights (user không có dòng restApiAllowForm thì được mọi form).
        /// v3 kiểm quyền form qua restApi_GetUserRights, mặc định từ chối
        /// </summary>
        public bool CheckFormRights(string form, string userName)
        {
            var ds = database.Retrieve("exec restApi_CheckFormRights @form, @userName",
            new SqlParameter { ParameterName = "@form", Value = form },
            new SqlParameter { ParameterName = "@userName", Value = userName }
            );

            if (ds.Tables[0].Rows.Count > 0)
            {
                return ds.Tables[0].Rows[0]["value"].ToString() == "1";
            }

            return false;
        }
    }
}
