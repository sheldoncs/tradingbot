using System;

namespace MetaTrader.Util
{
    public class TEEvent
    {
        public string Country { get; set; }
        public string Category { get; set; }
        public string Event { get; set; }
        public DateTime? Date { get; set; }
        public string Importance { get; set; }
    }

}
