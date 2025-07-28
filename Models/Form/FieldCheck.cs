namespace Models.Form
{
    public class FieldCheck
    {
        private string _condition, _errorCode, _script;
        private int _index;

        public string Condition { get => _condition; set => _condition = value; }
        public string ErrorCode { get => _errorCode; set => _errorCode = value; }
        public string Script { get => _script; set => _script = value; }
        public int Index { get => _index; set => _index = value; }

        public FieldCheck()
        {
            _condition = string.Empty;
            _errorCode = string.Empty;
            _script = string.Empty;
            _index = 0;
        }
    }
}
