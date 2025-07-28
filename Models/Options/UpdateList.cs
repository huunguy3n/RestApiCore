using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Options
{
    public class UpdateList : IList<Update>
    {
        private List<Update> _updates = new List<Update>();

        public Update this[int index]
        {
            get => _updates[index];
            set => _updates[index] = value;
        }

        public int Count => _updates.Count;

        public bool IsReadOnly => ((IList<Update>)_updates).IsReadOnly;

        public void Add(Update item)
        {
            _updates.Add(item);
        }

        public void Clear()
        {
            _updates.Clear();
        }

        public bool Contains(Update item)
        {
            return _updates.Contains(item);
        }

        public void CopyTo(Update[] array, int arrayIndex)
        {
            _updates.CopyTo(array, arrayIndex);
        }

        public Update Get(string controller)
        {
            return _updates.Find(o => o.controller == controller);
        }

        public IEnumerator<Update> GetEnumerator()
        {
            return _updates.GetEnumerator();
        }

        public int IndexOf(Update item)
        {
            return _updates.IndexOf(item);
        }

        public void Insert(int index, Update item)
        {
            _updates.Insert(index, item);
        }

        public bool Remove(Update item)
        {
            return _updates.Remove(item);
        }

        public void RemoveAt(int index)
        {
            _updates.RemoveAt(index);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _updates.GetEnumerator();
        }
    }
}
