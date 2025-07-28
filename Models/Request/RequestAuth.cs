using Models.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Request
{
    public class RequestAuth : IRequest
    {
        private DateTime _timeStamp;
        private string _ipAddress, _userName, _key;
        private long _time;

        public DateTime timeStamp { get => _timeStamp; }
        public string ipAddress { get => _ipAddress; set => _ipAddress = value; }
        public string userName { get => _userName; set => _userName = value; }
        public string key { get => _key; set => _key = value; }
        public long time { get => _time; set => _time = value; }

        public RequestAuth(string ipAddress, string userName)
        {
            _timeStamp = DateTime.Now;
            _ipAddress = ipAddress;
            _userName = userName;
            _key = string.Empty;
            _time = 0;
        }
    }
}
