using MetaTrader.Enums;
using System;

namespace MetaTrader.Data.Entities
{
    public class TradeInstruction
    {
        public string Action { get; set; }   // BUY | SELL | HOLD
        public double Lot { get; set; }
        public double SlPoints { get; set; }
        public double TpPoints { get; set; }
        public int Magic { get; set; }
        public string Comment { get; set; }


        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

}
