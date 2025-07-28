namespace Models.Form
{
    public class Form
    {
        private string _name;
        private FormType _type;
        private FormMethod _method;
        private string _id;
        private bool _enable;
        private List<Struct> _structs;
        private Dictionary<QueryAction, string> _queries;
        private DateTime _lastModified;

        public string Name { get => _name; private set => _name = value; }
        public FormType Type { get => _type; set => _type = value; }
        public FormMethod Method { get => _method; set => _method = value; }
        public string Id { get => _id; set => _id = value; }
        public bool Enable { get => _enable; set => _enable = value; }
        public List<Struct> Structs { get => _structs; set => _structs = value; }
        public Dictionary<QueryAction, string> Queries { get => _queries; set => _queries = value; }
        public DateTime LastModified { get => _lastModified; set => _lastModified = value; }

        public Form(string name)
        {
            _name = name;
            _type = FormType.List;
            _method = FormMethod.Set;
            _id = string.Empty;
            _enable = true;
            _structs = new List<Struct>();
            _queries = new Dictionary<QueryAction, string>();
            _lastModified = DateTime.Now;
        }
    }
}
