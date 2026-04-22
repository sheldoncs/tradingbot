namespace MetaTrader.Data.Entities
{
    public class MT5Command
    {
        public string type { get; set; } // "order" or "subscribe"
        public string symbol { get; set; }
        public string side { get; set; } // "buy" or "sell"
        public double volume { get; set; }
        public double stopLoss { get; set; } = 0;
        public double takeProfit { get; set; } = 0;
    }
}
