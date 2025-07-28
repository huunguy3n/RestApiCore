using Models.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Request
{
    public class Request : IRequest
    {
        private DateTime _timeStamp;
        private string _ipAddress, _userName;

        public DateTime timeStamp { get => _timeStamp; }
        public string ipAddress { get => _ipAddress; set => _ipAddress = value; }
        public string userName { get => _userName; set => _userName = value; }

        public Request(string ipAddress)
        {
            _timeStamp = DateTime.Now;
            _ipAddress = ipAddress;
            _userName = string.Empty;
        }
    }
}
