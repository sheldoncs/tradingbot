using MetaTrader.Data.Entities;
using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Helper
{
    public static class DollarProfitProtectionEngine
    {
        public static void Manage(
            TrackedTrade t,
            double midPrice,
            Action<TrackedTrade> modifyTrade,
            out bool isClosingTrade
        )
        {
            isClosingTrade = false;

            // --------------------------------------------------
            // 🔥 1. TRACK PEAK PROFIT
            // --------------------------------------------------
            t.MaxProfitUsd = Math.Max(t.MaxProfitUsd, t.ProfitUsd);

            // --------------------------------------------------
            // 🧠 NEW: STRONG TRADE DETECTION
            // --------------------------------------------------
            bool isStrongTrade =
                t.MaxProfitUsd >= 10 &&   // already proven
                t.ProfitUsd > 7;          // still holding gains

            // --------------------------------------------------
            // 🛑 2. GIVEBACK PROTECTION (🔥 PRIORITY FIRST)
            // --------------------------------------------------
            if (t.MaxProfitUsd >= 8)
            {
                double allowedGiveback =
                 isStrongTrade
                     ? (IsGold(t.Symbol) ? 7 :
                        IsCrypto(t.Symbol) ? 7 :
                        IsIndex(t.Symbol) ? 9 :
                        6)   // FX
                     : (IsGold(t.Symbol) ? 5 :
                        IsCrypto(t.Symbol) ? 6 :
                        IsIndex(t.Symbol) ? 7 :
                        4);

                double floor = t.MaxProfitUsd - allowedGiveback;

                if (t.ProfitUsd <= floor)
                {
                    LogFile.Write(
                        $"[{t.Symbol}] 🛑 GIVEBACK EXIT | profit={t.ProfitUsd:F2}, max={t.MaxProfitUsd:F2}, floor={floor:F2}, strong={isStrongTrade}"
                    );

                    isClosingTrade = true;
                    return;
                }
            }

            // --------------------------------------------------
            // 🛡 3. BREAK EVEN ($7)
            // --------------------------------------------------
            if (t.ProfitUsd >= 7 && !t.IsBreakEvenSet)
            {
                double buffer = IsGold(t.Symbol) ? 1.0 : 0.3;

                t.StopLoss =
                    t.Side == TradeSide.Buy
                        ? t.EntryPrice + buffer
                        : t.EntryPrice - buffer;

                modifyTrade?.Invoke(t);

                t.IsBreakEvenSet = true;

                LogFile.Write($"[{t.Symbol}] ✅ BE+ activated at $7");
            }

            // --------------------------------------------------
            // 🔒 4. PROFIT LOCK ($10 → keep %)
            // --------------------------------------------------
            if (t.MaxProfitUsd >= 10)
            {
                double lockPercent = isStrongTrade ? 0.7 : 0.5; // 🔥 stronger trades keep more

                double lockedProfit = t.MaxProfitUsd * lockPercent;

                double newSl =
                    t.Side == TradeSide.Buy
                        ? t.EntryPrice + lockedProfit
                        : t.EntryPrice - lockedProfit;

                bool shouldUpdate =
                    (t.Side == TradeSide.Buy && newSl > t.StopLoss) ||
                    (t.Side == TradeSide.Sell && newSl < t.StopLoss);

                if (shouldUpdate)
                {
                    t.StopLoss = newSl;
                    modifyTrade?.Invoke(t);

                    LogFile.Write($"[{t.Symbol}] 🔒 Profit locked ({lockPercent:P0})");
                }
            }

            // --------------------------------------------------
            // 📉 5. AGGRESSIVE TRAIL ($12+)
            // --------------------------------------------------
            if (t.MaxProfitUsd >= 12)
            {
                double trailDistance =
                    isStrongTrade
                        ? (IsGold(t.Symbol) ? 7 : 6)  // 🔥 wider trail
                        : 5;

                double trailBase =
                    t.Side == TradeSide.Buy
                        ? t.EntryPrice + t.MaxProfitUsd
                        : t.EntryPrice - t.MaxProfitUsd;

                double newSl =
                    t.Side == TradeSide.Buy
                        ? trailBase - trailDistance
                        : trailBase + trailDistance;

                bool shouldTrail =
                    (t.Side == TradeSide.Buy && newSl > t.StopLoss) ||
                    (t.Side == TradeSide.Sell && newSl < t.StopLoss);

                if (shouldTrail)
                {
                    t.StopLoss = newSl;
                    modifyTrade?.Invoke(t);

                    LogFile.Write($"[{t.Symbol}] 📉 Aggressive trailing active (strong={isStrongTrade})");
                }
            }
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") || symbol.Contains("XAG");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("SPX");
        }

        private static bool IsFx(string symbol)
        {
            return !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);
        }
    }
}
