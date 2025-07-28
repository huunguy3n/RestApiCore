using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Form
{
    public class FieldReference
    {
        private string _controller, _name, _script;
        public string Controller { get => _controller; set => _controller = value; }
        public string Name { get => _name; set => _name = value; }
        public string Script { get => _script; set => _script = value; }

        public FieldReference()
        {
            _controller = string.Empty;
            _name = string.Empty;
            _script = string.Empty;
        }

    }
}
