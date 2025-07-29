using Models.Interface;

namespace Models.Log
{
    public class LogData : ILog
    {
        public DateTime timeStamp { get; set; }
        public string ip { get; set; }
        public string controller { get; set; }
        public IRequest request { get; set; }
        public IResponse response { get; set; }

        public LogData(IRequest request, IResponse response)
        {
            timeStamp = DateTime.Now;
            ip = request.ipAddress;
            controller = request.userName;
            this.request = request;
            this.response = response;
        }
    }
}
