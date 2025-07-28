using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Response
{
    public class ResponseData
    {
        public int totalRowCount { get; set; }
        public int pageIndex { get; set; }
        public int pageSize { get; set; }
        public int pageCount { get; set; }
        public DataTable data { get; set; }

        public ResponseData()
        {
            totalRowCount = -1;
            pageIndex = -1;
            pageSize = -1;
            pageCount = -1;
            data = null;
        }
    }
}
