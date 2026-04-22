using MetaTrader.Enums;

namespace MetaTrader.Data.Entities
{
    public class TradeDecision
    {
        public TradeAction Action { get; set; }
        public double LotMultiplier { get; set; } = 1.0;
        public bool H4Weak { get; set; }
        public bool H4Reversal { get; set; }
        public double H4Momentum { get; set; }
        public double Momentum { get; set; }
    }
}
