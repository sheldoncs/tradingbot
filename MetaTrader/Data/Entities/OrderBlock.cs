using System;

namespace MetaTrader.Data.Entities
{
    public class OrderBlock
    {
        public double High { get; set; }
        public double Low { get; set; }
        public bool IsBullish { get; set; }
        public DateTime Time { get; set; }
    }
}
