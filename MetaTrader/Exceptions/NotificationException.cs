using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingApplication.Exceptions
{
    public class NotificationException : Exception
    {
        public NotificationException()
        {
        }

        public NotificationException(string message) : base(message)
        {
        }

        public NotificationException(String msg, Exception inner) : base(msg, inner)
        {

        }
    }
}
