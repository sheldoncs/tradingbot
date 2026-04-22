using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingApplication.Exceptions
{
    public class KryptoException : Exception
    {
        public KryptoException()
        {
        }

        public KryptoException(string message) : base(message)
        {
        }

        public KryptoException(String msg, Exception inner) : base(msg, inner)
        {

        }
    }
}
