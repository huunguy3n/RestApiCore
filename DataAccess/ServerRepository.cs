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
    }
}
