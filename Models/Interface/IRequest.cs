using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Interface
{
    public interface IRequest
    {
        DateTime timeStamp { get; }
        string ipAddress { get; set; }
        string userName { get; set; }
    }
}
