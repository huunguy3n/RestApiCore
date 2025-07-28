using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataAccess
{
    public class ServerRepository
    {
        private readonly AppConfig _appConfig;

        public ServerRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
            Provider.Provider.CommandTimeout = _appConfig.commandTimeout;
        }

        public bool CheckIPAddress(string clientIP, string userName)
        {
            var ds = Provider.Provider.Retrieve(_appConfig.appConnectionString, "exec restApi_CheckIPAddress @ip, @userName",
                new System.Data.SqlClient.SqlParameter { ParameterName = "@ip", Value = clientIP },
                new System.Data.SqlClient.SqlParameter { ParameterName = "@userName", Value = userName }
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
            new System.Data.SqlClient.SqlParameter { ParameterName = "@form", Value = form },
            new System.Data.SqlClient.SqlParameter { ParameterName = "@userName", Value = userName }
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
