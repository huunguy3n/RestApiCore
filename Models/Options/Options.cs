using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Options
{
    public class Options
    {
        private string _irregular;
        private MessageList _messages;
        private UpdateList _updates;

        public string Irregular { get => _irregular; set => _irregular = value; }
        public MessageList Messages { get => _messages; set => _messages = value; }
        public UpdateList Updates { get => _updates; set => _updates = value; }

        public Options()
        {
            _irregular = string.Empty;
            _messages = new MessageList();
            _updates = new UpdateList();
        }

    }
}
