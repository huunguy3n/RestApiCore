using Models.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Business
{
    public static class LogManager
    {

        public static void WriteLog(IRequest request, IResponse response)
        {
            // Implement logging logic here
        }

        //LogActivity(userName, action)
        public static void WriteLogActivity(IRequest request)
        {
            // Implement logging logic for user activity
        }

        //LogData(userName, form, filePath)
        public static void WriteLogData(IRequest request, IResponse response)
        {

        }

        //LogError(action, errorMessage, stackTrace)
        public static void WriteLogError(string action, string errorMessage, string stackTrace)
        {
            // Implement logging logic for errors
        }

        //LogTransfer(userName, form, key, message, status)
        public static void WriteLogTransfer(IRequest request, string key, string message, bool status)
        {
            // Implement logging logic for data transfer
        }


    }
}
