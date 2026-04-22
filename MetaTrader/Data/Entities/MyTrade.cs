namespace MetaTrader.Data.Entities
{
    public class MyTrade
    {
        public long Ticket { get; set; }        // MT5 trade ticket
        public string Symbol { get; set; }      // e.g. "XAUUSD"
        public TradeSide Side { get; set; }     // Buy or Sell
        public double EntryPrice { get; set; }
        public double Volume { get; set; }      // lot size
    }
}
