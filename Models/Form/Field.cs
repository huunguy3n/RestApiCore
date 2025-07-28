using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Form
{
    public class Field
    {
        private string _name, _mapping;
        private FieldType _type;
        private string _dataFormatString;
        private int _maxLength;
        private bool _upperCase, _allowNulls, _isPrimaryKey, _checkIrregular;
        private string _defaultValue;
        private bool _hidden;
        private string _sqlType;
        private FieldReference _reference;
        private FieldCheck _check;

        public string Name { get => _name; set => _name = value; }
        public string Mapping { get => _mapping; set => _mapping = value; }
        public FieldType Type { get => _type; set => _type = value; }
        public string DataFormatString { get => _dataFormatString; set => _dataFormatString = value; }
        public int MaxLength { get => _maxLength; set => _maxLength = value; }
        public bool UpperCase { get => _upperCase; set => _upperCase = value; }
        public bool AllowNulls { get => _allowNulls; set => _allowNulls = value; }
        public bool IsPrimaryKey { get => _isPrimaryKey; set => _isPrimaryKey = value; }
        public bool CheckIrregular { get => _checkIrregular; set => _checkIrregular = value; }
        public string DefaultValue { get => _defaultValue; set => _defaultValue = value; }
        public bool Hidden { get => _hidden; set => _hidden = value; }
        public string SqlType { get => _sqlType; set => _sqlType = value; }
        public FieldReference Reference { get => _reference; set => _reference = value; }
        public FieldCheck Check { get => _check; set => _check = value; }

        public Field()
        {
            _name = string.Empty;
            _mapping = string.Empty;
            _type = FieldType.String;
            _dataFormatString = string.Empty;
            _maxLength = -1;
            _upperCase = false;
            _allowNulls = true;
            _isPrimaryKey = false;
            _checkIrregular = false;
            _defaultValue = string.Empty;
            _hidden = false;
            _sqlType = string.Empty;
            _reference = new FieldReference();
            _check = new FieldCheck();
        }
    }
}
