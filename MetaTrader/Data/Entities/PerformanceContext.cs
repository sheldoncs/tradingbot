using MetaTrader.Util;

namespace MetaTrader.Data.Entities
{
    public sealed class PerformanceContext
    {
        public double SessionPnl { get; init; }
        public double SessionPnlPct { get; init; }

        public double WeekPnl { get; init; }
        public double WeekPnlPct { get; init; }

        public int TradesToday { get; init; }
        public int ExpectedTrades { get; init; }

        public double WinRate { get; init; }
        public double AvgRiskReward { get; init; }

        public double MaxDrawdownAllowedPct { get; init; }
        public double DrawdownUsedPct { get; init; }

        public RiskMode RiskMode { get; init; }
        public string RiskModeText { get; init; }
    }

}
