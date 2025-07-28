using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models
{
    public class Client
    {
        private string _userName, _ip;

        public string UserName { get => _userName; set => _userName = value; }
        public string Ip { get => _ip; set => _ip = value; }

        public Client()
        {
            _userName = string.Empty;
            _ip = string.Empty;
        }
    }
}
