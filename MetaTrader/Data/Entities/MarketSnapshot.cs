using System.Collections.Generic;

namespace MetaTrader.Data.Entities
{
    public class MarketSnapshot
    {
        //public string Symbol { get; set; } = "XAUUSD";
        public string Timeframe { get; set; } = "H1";

        // Last N candles
        public List<TrackedTrade> Candles { get; set; } = new();

        // Indicators (you fill these from your existing code)
        public double EmaFast { get; set; }
        public double EmaSlow { get; set; }
        public double Atr { get; set; }
        public double EmaDistancePoints { get; set; }
        public double ChopValue { get; set; }

        // Optional higher timeframe info
        public double H4EmaFast { get; set; }
        public double H4EmaSlow { get; set; }
        public double H4Momentum { get; set; }
    }
}
