using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services.Helper
{
    public static class TradeTrackingService
    {
        private class TradeInfo
        {
            public DateTime EntryTimeUtc { get; set; }
            public double MaxProfitUsd { get; set; }
        }

        private static readonly Dictionary<ulong, TradeInfo> _trades = new();

        // =========================
        // 1️⃣ Register new trade
        // =========================
        public static void RegisterTrade(ulong ticket)
        {
            _trades[ticket] = new TradeInfo
            {
                EntryTimeUtc = DateTime.UtcNow,
                MaxProfitUsd = 0
            };
        }

        // =========================
        // 2️⃣ Update profit tracking
        // =========================
        public static void UpdateProfit(ulong ticket, double currentProfitUsd)
        {
            if (!_trades.TryGetValue(ticket, out var info))
                return;

            info.MaxProfitUsd = Math.Max(info.MaxProfitUsd, currentProfitUsd);
        }

        // =========================
        // 3️⃣ Get max profit
        // =========================
        public static double GetMaxProfit(ulong ticket)
        {
            return _trades.TryGetValue(ticket, out var info)
                ? info.MaxProfitUsd
                : 0;
        }

        // =========================
        // 4️⃣ Get candles since entry
        // =========================
        public static int GetCandlesSinceEntry(ulong ticket, MqlRates[] rates)
        {
            if (!_trades.TryGetValue(ticket, out var info))
                return 0;

            int count = 0;

            for (int i = rates.Length - 2; i >= 0; i--) // closed candles only
            {
                if (rates[i].time <= info.EntryTimeUtc)
                    break;

                count++;
            }

            return count;
        }

        // =========================
        // 5️⃣ Stall Exit Evaluation (UPDATED)
        // =========================
        public static (bool ShouldExit, string Reason) EvaluateStallExit(
            ulong ticket,
            string symbol,
            double currentProfitUsd,
            double currentMomentum,
            double entryPrice,
            double currentPrice,
            double atr,
            MqlRates[] rates)
        {
            if (!_trades.TryGetValue(ticket, out var info))
                return (false, null);

            int candlesSinceEntry = GetCandlesSinceEntry(ticket, rates);
            double maxProfitSeen = info.MaxProfitUsd;
            double distanceFromEntryAtr = Math.Abs(currentPrice - entryPrice) / atr;

            // -------------------------
            // 🔥 EARLIER EXIT (2 candles)
            // -------------------------
            if (candlesSinceEntry < 2)
                return (false, null);

            bool weakMomentum = currentMomentum < GetMomentumThreshold(symbol);
            bool lowProfit = currentProfitUsd < GetMinProfit(symbol);
            bool failedToExpand = maxProfitSeen < GetMinPeakProfit(symbol);
            bool stuck = distanceFromEntryAtr < GetMinDistance(symbol);

            // -------------------------
            // 🔥 CORE EXIT LOGIC (FIXED)
            // -------------------------
            bool shouldExit =
                (weakMomentum && lowProfit) ||          // stalled quickly
                (weakMomentum && failedToExpand) ||     // moved but failed (YOUR ETH TRADE)
                (weakMomentum && stuck);                // stuck near entry

            if (shouldExit)
            {
                string reason =
                    $"StallExit | candles={candlesSinceEntry}, " +
                    $"profit={currentProfitUsd:F2}, " +
                    $"maxProfit={maxProfitSeen:F2}, " +
                    $"momentum={currentMomentum:F2}, " +
                    $"distATR={distanceFromEntryAtr:F2}";

                LogFile.Write($"[{symbol}] ⚠️ {reason}");

                return (true, reason);
            }

            return (false, null);
        }

        // =========================
        // 6️⃣ Cleanup on close
        // =========================
        public static void RemoveTrade(ulong ticket)
        {
            _trades.Remove(ticket);
        }

        // =========================
        // 🔧 Thresholds
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
