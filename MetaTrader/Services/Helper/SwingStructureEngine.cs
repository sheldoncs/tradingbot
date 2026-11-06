using System;
using global::MtApi5;
using global::MetaTrader.Enums;

namespace MetaTrader.Services.Helper
{
    /// <summary>
    /// Stateless, non-repainting CLOSED-H1 swing detection with a LIVE bid/ask
    /// break-of-structure candidate. No account, position or order side effects.
    /// Each call uses only its own symbol's candle history and live prices.
    /// This engine never rejects a pre-existing FastEntry signal.
    /// </summary>
    public static class SwingStructureEngine
    {
        public enum StructureDirection { Unknown, Bullish, Bearish, Mixed }

        public sealed class Snapshot
        {
            public string Symbol { get; set; }
            public StructureDirection Direction { get; set; }
            public double? LastSwingHigh { get; set; }
            public double? PreviousSwingHigh { get; set; }
            public double? LastSwingLow { get; set; }
            public double? PreviousSwingLow { get; set; }
            public DateTime? HighTime { get; set; }
            public DateTime? LowTime { get; set; }
            public bool HigherHigh { get; set; }
            public bool HigherLow { get; set; }
            public bool LowerHigh { get; set; }
            public bool LowerLow { get; set; }
        }

        public sealed class LiveBreakCandidate
        {
            public TradeAction Action { get; set; } = TradeAction.DoNothing;
            public double BrokenLevel { get; set; }
            public double ExtensionAtr { get; set; }
            public double BodyAtr { get; set; }
            public Snapshot Structure { get; set; }
            public bool HasSignal => Action == TradeAction.EnterBuy || Action == TradeAction.EnterSell;
        }

        // Two bars must CLOSE after the pivot. No look-ahead/repainting.
        // `closedH1` must exclude the current forming candle (MT5 CopyRates shift=1).
        public static Snapshot Analyze(string symbol, MqlRates[] closedH1,
            int leftBars = 2, int rightBars = 2, int lookback = 80)
        {
            var result = new Snapshot { Symbol = symbol ?? string.Empty, Direction = StructureDirection.Unknown };
            if (closedH1 == null || leftBars < 1 || rightBars < 1 ||
                closedH1.Length < leftBars + rightBars + 5) return result;

            // Incoming history is expected oldest -> newest, as used by FastEntry.
            int last = closedH1.Length - 1 - rightBars;
            int first = Math.Max(leftBars, closedH1.Length - Math.Max(lookback, leftBars + rightBars + 5));
            for (int i = first; i <= last; i++)
            {
                bool high = true, low = true;
                for (int j = i - leftBars; j <= i + rightBars; j++)
                {
                    if (j == i) continue;
                    // Strict unique pivot: equal highs/lows are not confirmed swings.
                    if (closedH1[j].high >= closedH1[i].high) high = false;
                    if (closedH1[j].low <= closedH1[i].low) low = false;
                    if (!high && !low) break;
                }
                if (high)
                {
                    result.PreviousSwingHigh = result.LastSwingHigh;
                    result.LastSwingHigh = closedH1[i].high;
                    result.HighTime = closedH1[i].time;
                }
                if (low)
                {
                    result.PreviousSwingLow = result.LastSwingLow;
                    result.LastSwingLow = closedH1[i].low;
                    result.LowTime = closedH1[i].time;
                }
            }
            if (result.LastSwingHigh.HasValue && result.PreviousSwingHigh.HasValue)
            {
                result.HigherHigh = result.LastSwingHigh.Value > result.PreviousSwingHigh.Value;
                result.LowerHigh = result.LastSwingHigh.Value < result.PreviousSwingHigh.Value;
            }
            if (result.LastSwingLow.HasValue && result.PreviousSwingLow.HasValue)
            {
                result.HigherLow = result.LastSwingLow.Value > result.PreviousSwingLow.Value;
                result.LowerLow = result.LastSwingLow.Value < result.PreviousSwingLow.Value;
            }
            if (result.HigherHigh && result.HigherLow) result.Direction = StructureDirection.Bullish;
            else if (result.LowerHigh && result.LowerLow) result.Direction = StructureDirection.Bearish;
            else if (result.LastSwingHigh.HasValue && result.LastSwingLow.HasValue)
                result.Direction = StructureDirection.Mixed;
            return result;
        }

        /// <summary>
        /// Optional ADDITIVE entry path. Called ONLY when the existing live
        /// impulse detector returns no signal. It can never veto an entry.
        /// Checks a live break of a confirmed closed-candle pivot near its level.
        /// The normal FastEntry S/R, M15, follow-through and execution guards
        /// still process an approved candidate afterwards.
        /// </summary>
        public static LiveBreakCandidate TryDetectLiveBreak(
            string symbol, MqlRates[] closedH1, double atr, double bid, double ask,
            double liveOpen, double liveEma9, double liveEma21, double hybridMomentum)
        {
            var candidate = new LiveBreakCandidate();
            if (closedH1 == null || closedH1.Length < 12 || atr <= 0 ||
                bid <= 0 || ask < bid || liveOpen <= 0 ||
                liveEma9 <= 0 || liveEma21 <= 0) return candidate;

            var swing = Analyze(symbol, closedH1);
            candidate.Structure = swing;
            double mid = (bid + ask) * 0.5;
            double bodyAtr = Math.Abs(mid - liveOpen) / atr;
            candidate.BodyAtr = bodyAtr;

            // Parameters govern ONLY this new alternative entry path. They
            // do NOT add restrictions to existing impulse/continuation signals.
            const double minHybrid = 0.35;
            const double minLiveBodyAtr = 0.12;
            const double maxPastSwingAtr = 0.70;
            if (hybridMomentum < minHybrid || bodyAtr < minLiveBodyAtr) return candidate;

            // Do not require prior HH+HL or LH+LL: the FIRST structure break
            // may be an early change of character. Avoid shorting a bullish
            // live EMA regime, or buying a bearish one.
            if (swing.LastSwingHigh.HasValue &&
                ask > swing.LastSwingHigh.Value &&
                closedH1[closedH1.Length - 1].close <= swing.LastSwingHigh.Value &&
                mid > liveOpen && liveEma9 >= liveEma21 && mid >= liveEma9)
            {
                double extension = (ask - swing.LastSwingHigh.Value) / atr;
                if (extension >= 0 && extension <= maxPastSwingAtr)
                {
                    candidate.Action = TradeAction.EnterBuy;
                    candidate.BrokenLevel = swing.LastSwingHigh.Value;
                    candidate.ExtensionAtr = extension;
                }
            }
            else if (swing.LastSwingLow.HasValue &&
                bid < swing.LastSwingLow.Value &&
                closedH1[closedH1.Length - 1].close >= swing.LastSwingLow.Value &&
                mid < liveOpen && liveEma9 <= liveEma21 && mid <= liveEma9)
            {
                double extension = (swing.LastSwingLow.Value - bid) / atr;
                if (extension >= 0 && extension <= maxPastSwingAtr)
                {
                    candidate.Action = TradeAction.EnterSell;
                    candidate.BrokenLevel = swing.LastSwingLow.Value;
                    candidate.ExtensionAtr = extension;
                }
            }
            return candidate;
        }
    }
}
