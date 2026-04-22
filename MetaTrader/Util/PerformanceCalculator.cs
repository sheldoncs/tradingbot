using MetaTrader.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Util
{
    public enum RiskMode
    {
        Normal,
        Caution,
        Locked
    }

    public sealed class ClosedTrade
    {
        public DateTime CloseTime { get; init; }
        public double Profit { get; init; }      // $ value
        public double Risk { get; init; }        // $ risked
        public bool IsWin => Profit > 0;
        public string Symbol { get; init; }
        public double EntryPrice { get; init; }
        public double StopLoss { get; init; }
        public double Volume { get; init; }
    }

    public sealed class PerformanceCalculator
    {
        private readonly double _startingEquity;
        private readonly double _maxDrawdownPct;
        private readonly int _expectedTradesPerDay;

        public PerformanceCalculator(
            double startingEquity,
            double maxDrawdownPct,
            int expectedTradesPerDay
        )
        {
            _startingEquity = startingEquity;
            _maxDrawdownPct = maxDrawdownPct;
            _expectedTradesPerDay = expectedTradesPerDay;
        }

        public PerformanceContext Calculate(
            double currentEquity,
            IEnumerable<ClosedTrade> closedTrades,
            DateTime nowUtc
        )
        {
            var trades = closedTrades.ToList();

            // -----------------------------
            // Session (today)
            // -----------------------------
            var today = nowUtc.Date;
            var sessionTrades = trades.Where(t => t.CloseTime.Date == today).ToList();

            double sessionPnl = sessionTrades.Sum(t => t.Profit);
            double sessionPnlPct = Percent(sessionPnl, _startingEquity);

            // -----------------------------
            // Week
            // -----------------------------
            var weekStart = StartOfWeek(today);
            var weekTrades = trades.Where(t => t.CloseTime.Date >= weekStart).ToList();

            double weekPnl = weekTrades.Sum(t => t.Profit);
            double weekPnlPct = Percent(weekPnl, _startingEquity);

            // -----------------------------
            // Win rate / RR
            // -----------------------------
            double winRate = trades.Any()
                ? trades.Count(t => t.IsWin) / (double)trades.Count
                : 0.0;

            double avgRR = trades.Any(t => t.Risk > 0)
                ? trades.Average(t => Math.Abs(t.Profit / t.Risk))
                : 0.0;

            // -----------------------------
            // Drawdown
            // -----------------------------
            double drawdownUsedPct = Percent(
                _startingEquity - currentEquity,
                _startingEquity
            );

            // -----------------------------
            // Risk mode
            // -----------------------------
            var riskMode = ResolveRiskMode(drawdownUsedPct);

            return new PerformanceContext
            {
                SessionPnl = sessionPnl,
                SessionPnlPct = sessionPnlPct,

                WeekPnl = weekPnl,
                WeekPnlPct = weekPnlPct,

                TradesToday = sessionTrades.Count,
                ExpectedTrades = _expectedTradesPerDay,

                WinRate = Math.Round(winRate * 100, 1),
                AvgRiskReward = Math.Round(avgRR, 2),

                MaxDrawdownAllowedPct = _maxDrawdownPct,
                DrawdownUsedPct = Math.Round(drawdownUsedPct, 2),

                RiskMode = riskMode,
                RiskModeText = riskMode.ToString()
            };
        }

        // -----------------------------
        // Helpers
        // -----------------------------

        private static double Percent(double value, double baseValue)
            => baseValue == 0 ? 0 : (value / baseValue) * 100.0;

        private static DateTime StartOfWeek(DateTime date)
            => date.AddDays(-(int)date.DayOfWeek);

        private RiskMode ResolveRiskMode(double ddUsedPct)
        {
            if (ddUsedPct >= _maxDrawdownPct)
                return RiskMode.Locked;

            if (ddUsedPct >= _maxDrawdownPct * 0.7)
                return RiskMode.Caution;

            return RiskMode.Normal;
        }
    }

}
