using MetaTrader.Enums;
using System;

namespace MetaTrader.Data.Entities
{
    public class SymbolContextSnapshot
    {
        public string Symbol { get; set; }
        public MarketState State { get; set; }
        public int PullbackCandles { get; set; }
        public int ExpansionCandles { get; set; }
        public double PeakAtr { get; set; }
        public bool PriceAccepted { get; set; }
        public bool AtrCooldown { get; set; }
        public bool ShockActive { get; set; }
        public DateTime LastProcessedCandleTime { get; set; }
        public DateTime SavedAtUtc { get; set; }
    }
}
