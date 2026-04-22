namespace MetaTrader.Data.Entities
{
    public class MT5PriceUpdate
    {
        public string symbol { get; set; }
        public double bid { get; set; }
        public double ask { get; set; }
        public double last { get; set; }
    }
}
