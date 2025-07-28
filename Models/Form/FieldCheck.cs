using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Form
{
    public class FieldCheck
    {
        private string _condition, _errorCode;

        public string Condition { get => _condition; set => _condition = value; }
        public string ErrorCode { get => _errorCode; set => _errorCode = value; }

        public FieldCheck()
        {
            _condition = string.Empty;
            _errorCode = string.Empty;
        }
    }
}
