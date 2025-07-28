namespace Models.Form
{
    public class FieldReference
    {
        private string _controller, _name, _script;
        private int _index;

        public string Controller { get => _controller; set => _controller = value; }
        public string Name { get => _name; set => _name = value; }
        public string Script { get => _script; set => _script = value; }
        public int Index { get => _index; set => _index = value; }

        public FieldReference()
        {
            _controller = string.Empty;
            _name = string.Empty;
            _script = string.Empty;
            _index = 0;
        }

    }
}
