using Microsoft.Data.SqlClient;
using Models;

namespace DataAccess
{
    public class ServerRepository
    {
        private readonly AppConfig _appConfig;

        public ServerRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
        }

        public async Task<bool> CheckIPAddressAsync(string clientIP, string userName, CancellationToken cancellationToken = default)
        {
            var ds = await Provider.Provider.RetrieveAsync(_appConfig.appConnectionString, "exec restApi_CheckIPAddress @ip, @userName", cancellationToken,
                new SqlParameter { ParameterName = "@ip", Value = clientIP },
                new SqlParameter { ParameterName = "@userName", Value = userName }
                );

            if (ds != null)
            {
                if (ds.Tables[0].Rows.Count > 0)
                {
                    return ds.Tables[0].Rows[0]["value"].ToString() == "1";
                }
            }

            return false;
        }

        public bool CheckFormRights(string form, string userName)
        {
            //Kiểm tra quyền truy cập
            var ds = Provider.Provider.Retrieve(_appConfig.appConnectionString, "exec restApi_CheckFormRights @form, @userName",
            new SqlParameter { ParameterName = "@form", Value = form },
            new SqlParameter { ParameterName = "@userName", Value = userName }
            );

            if (ds != null)
            {
                if (ds.Tables[0].Rows.Count > 0)
                {
                    return ds.Tables[0].Rows[0]["value"].ToString() == "1";
                }
            }

            return false;
        }
    }
}
