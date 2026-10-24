using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Request
{
    public class RequestQuery
    {
        private DateTime _timeStamp;
        private string _ipAddress, _userName, _formName, _unit, _cacheID, _userERP;
        private string _jsonBody;
        private int _pageIndex;
        private bool _debug;

        public DateTime timeStamp { get => _timeStamp; }
        public string ipAddress { get => _ipAddress; set => _ipAddress = value; }
        public string userName { get => _userName; set => _userName = value; }
        public string formName { get => _formName; set => _formName = value; }
        public string unit { get => _unit; set => _unit = value; }
        public string cacheID { get => _cacheID; set => _cacheID = value; }
        public int pageIndex { get => _pageIndex; set => _pageIndex = value; }
        public string jsonBody { get => _jsonBody; set => _jsonBody = value; }
        public bool debug { get => _debug; set => _debug = value; }
        public string userERP { get => _userERP; set => _userERP = value; }


        public RequestQuery(string formName, string ipAddress, string userName)
        {
            _timeStamp = DateTime.Now;
            _ipAddress = ipAddress;
            _userName = userName;
            _formName = formName;
            _unit = string.Empty;
            cacheID = string.Empty;
            pageIndex = 0;
            _jsonBody = string.Empty;
            _debug = false;
            userERP = string.Empty;
        }
    }
}
