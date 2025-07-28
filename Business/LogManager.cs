using Models.Interface;

namespace Business
{
    public class LogManager
    {
        public void WriteLog(IRequest request, IResponse response)
        {
            // Implement logging logic here
        }

        //LogActivity(userName, action)
        public void WriteLogActivity(IRequest request)
        {
            // Implement logging logic for user activity
        }

        //LogData(userName, form, filePath)
        public void WriteLogData(IRequest request, IResponse response)
        {

        }

        //LogError(action, errorMessage, stackTrace)
        public void WriteLogError(string action, string errorMessage, string stackTrace)
        {
            // Implement logging logic for errors
        }

        //LogTransfer(userName, form, key, message, status)
        public void WriteLogTransfer(IRequest request, string key, string message, bool status)
        {
            // Implement logging logic for data transfer
        }


    }
}
