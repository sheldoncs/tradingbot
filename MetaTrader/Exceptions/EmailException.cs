using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingApplication.Exceptions
{
    public class EmailException : Exception
    {
        public EmailException()
        {
        }

        public EmailException(string message) : base(message)
        {
        }

        public EmailException(String msg, Exception inner) : base(msg, inner)
        {

        }
    }
}
