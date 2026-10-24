namespace Models.Options
{
    public class Options
    {
        private string _irregular;
        private MessageList _messages;

        public string Irregular { get => _irregular; set => _irregular = value; }
        public MessageList Messages { get => _messages; set => _messages = value; }

        public Options()
        {
            _irregular = string.Empty;
            _messages = new MessageList();
        }

    }
}
