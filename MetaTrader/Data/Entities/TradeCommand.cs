namespace MetaTrader.Data.Entities
{
    public class TradeCommand
    {
        public string Type { get; set; } = "TRADE";
        public string Symbol { get; set; }
        public string Action { get; set; }   // BUY / SELL
        public double Lot { get; set; }
        public int Magic { get; set; }
    }

}
