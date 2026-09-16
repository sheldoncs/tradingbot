using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Helper
{
    public static class TradeStallExitEngine
    {
        public class StallResult
        {
            public bool ShouldExit { get; set; }
            public string Reason { get; set; }
        }

        /// <summary>
        /// Determines if a trade should be closed due to lack of follow-through
        /// </summary>
        public static StallResult Evaluate(
            string symbol,
            int candlesSinceEntry,
            double currentProfitUsd,
            double maxProfitSeenUsd,
            double currentMomentum,
            double entryPrice,
            double currentPrice,
            double atr)
        {
            // =========================
            // 1️⃣ Minimum time in trade
            // =========================
            if (candlesSinceEntry < 3)
            {
                return new StallResult { ShouldExit = false };
            }

            // =========================
            // 2️⃣ Distance from entry (ATR-based)
            // =========================
            double distanceFromEntryAtr = Math.Abs(currentPrice - entryPrice) / atr;

            // =========================
            // 3️⃣ Core stall conditions
            // =========================
            bool noProgress = currentProfitUsd < GetMinProfit(symbol);
            bool neverMoved = maxProfitSeenUsd < GetMinPeakProfit(symbol);
            bool weakMomentum = currentMomentum < GetMomentumThreshold(symbol);
            bool stuck = distanceFromEntryAtr < GetMinDistance(symbol);

            // =========================
            // 4️⃣ Decision
            // =========================
            if (noProgress && neverMoved && weakMomentum && stuck)
            {
                string reason =
                    $"StallExit | profit={currentProfitUsd:F2}, " +
                    $"maxProfit={maxProfitSeenUsd:F2}, " +
                    $"momentum={currentMomentum:F2}, " +
                    $"distATR={distanceFromEntryAtr:F2}";

                LogFile.Write($"[{symbol}] ❌ {reason}");

                return new StallResult
                {
                    ShouldExit = true,
                    Reason = reason
                };
            }

            return new StallResult { ShouldExit = false };
        }

        // =========================
        // 🔧 Tunable thresholds
        // =========================

        private static double GetMinProfit(string symbol)
        {
            if (IsGold(symbol)) return 2.0;
            if (IsCrypto(symbol)) return 1.5;
            if (IsFx(symbol)) return 1.0;

            return 1.0;
        }

        private static double GetMinPeakProfit(string symbol)
        {
            if (IsGold(symbol)) return 4.0;
            if (IsCrypto(symbol)) return 3.0;
            if (IsFx(symbol)) return 2.0;

            return 2.0;
        }

        private static double GetMomentumThreshold(string symbol)
        {
            if (IsGold(symbol)) return 1.0;
            if (IsCrypto(symbol)) return 1.0;
            if (IsFx(symbol)) return 0.9;

            return 1.0;
        }

        private static double GetMinDistance(string symbol)
        {
            if (IsGold(symbol)) return 0.4;
            if (IsCrypto(symbol)) return 0.35;
            if (IsFx(symbol)) return 0.3;

            return 0.3;
        }

        private static bool IsGold(string symbol)
            => symbol.Contains("XAU") || symbol.Contains("XAG");

        private static bool IsCrypto(string symbol)
            => symbol.Contains("BTC") ||
               symbol.Contains("ETH") ||
               symbol.Contains("SOL") ||
               symbol.Contains("BNB") ||
               symbol.Contains("XRP") ||
               symbol.Contains("ADA");

        private static bool IsFx(string symbol)
            => !IsGold(symbol) && !IsCrypto(symbol);
    }
}
