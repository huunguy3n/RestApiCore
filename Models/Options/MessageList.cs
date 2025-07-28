using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Options
{
    public class MessageList : IList<Message>
    {
        private List<Message> _messages = new List<Message>();

        public Message this[int index]
        {
            get => _messages[index];
            set => _messages[index] = value;
        }

        public int Count => _messages.Count;

        public bool IsReadOnly => ((IList<Message>)_messages).IsReadOnly;

        public void Add(Message item)
        {
            _messages.Add(item);
        }

        public void Clear()
        {
            _messages.Clear();
        }

        public bool Contains(Message item)
        {
            return _messages.Contains(item);
        }

        public void CopyTo(Message[] array, int arrayIndex)
        {
            _messages.CopyTo(array, arrayIndex);
        }

        public Message Get(string code)
        {
            return _messages.Find(o => o.code == code);
        }

        public IEnumerator<Message> GetEnumerator()
        {
            return _messages.GetEnumerator();
        }

        public int IndexOf(Message item)
        {
            return _messages.IndexOf(item);
        }

        public void Insert(int index, Message item)
        {
            _messages.Insert(index, item);
        }

        public bool Remove(Message item)
        {
            return _messages.Remove(item);
        }

        public void RemoveAt(int index)
        {
            _messages.RemoveAt(index);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _messages.GetEnumerator();
        }
    }
}
