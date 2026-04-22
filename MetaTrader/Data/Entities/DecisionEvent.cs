using MetaTrader.Enums;
using System;

namespace MetaTrader.Data.Entities
{
    public class DecisionEvent
    {
        public string Symbol { get; set; }
        public string Action { get; set; }
        public string Category { get; set; }
        public string Reason { get; set; }

        public string Message { get; set; }

        public double Momentum { get; set; }
        public bool EarlyMomentum { get; set; }
        public bool EmaCompressed { get; set; }
        public string Regime { get; set; }

        public DateTime Time { get; set; }
    }
}
