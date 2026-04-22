using System;

namespace MetaTrader.Data.Entities
{
    public class TradeRecord
    {
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public TradeSide Side { get; set; }   // Buy / Sell
        public double Profit { get; set; }    // + / -
    }

}
