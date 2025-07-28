using Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataAccess
{
    public class LogRepository
    {
        private readonly AppConfig _appConfig;
        public LogRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
            Provider.Provider.CommandTimeout = _appConfig.commandTimeout;
        }
    }
}
