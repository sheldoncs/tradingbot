using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MetaTrader.Util
{
    public class Results<T>
    {
        public T Data { get; set; }
        public int status { get; set; }
        public string message { get; set; }
        public string messageType { get; set; }
        public string token { get; set; }
    }
}
