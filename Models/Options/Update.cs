using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Options
{
    public class Update
    {
        public string controller { get; set; }
        public string query { get; set; }

        public Update()
        {
            controller = string.Empty;
            query = string.Empty;
        }
    }
}
