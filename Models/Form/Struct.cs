using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Form
{
    public class Struct
    {
        private string _name, _baseTable;
        private List<Field> _fields;

        public string Name { get => _name; set => _name = value; }
        public string BaseTable { get => _baseTable; set => _baseTable = value; }
        public List<Field> Fields { get => _fields; set => _fields = value; }

        public Struct()
        {
            _name = string.Empty;
            _baseTable = string.Empty;
            _fields = new List<Field>();
        }
    }
}
