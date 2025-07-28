using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models
{
    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string NameErp { get; set; }
        public string Password { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsGroup { get; set; }
        public bool AccessYN { get; set; }
        public bool NewYN { get; set; }
        public bool EditYN { get; set; }
        public bool DeleteYN { get; set; }
        public string UserList { get; set; }
        public char Status { get; set; }
        public int? UserId0 { get; set; }
        public int? UserId2 { get; set; }
        public DateTime? DateTime0 { get; set; }
        public DateTime? DateTime2 { get; set; }

        public User()
        {
            Id = 0;
            Name = string.Empty;
            NameErp = string.Empty;
            Password = string.Empty;
            IsAdmin = false;
            IsGroup = false;
            AccessYN = false;
            NewYN = false;
            EditYN = false;
            DeleteYN = false;
            Status = '0'; //0 Deactive, 1 Actvie
            UserId0 = null;
            UserId2 = null;
            DateTime0 = null;
            DateTime2 = null;
        }
    }
}
