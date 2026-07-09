using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class StallExitEngine
    {
        public static bool ShouldExit(
            string symbol,
            TradeAction side,
            MqlRates[] rates,
            double entryPrice,
            double currentPrice,
            double atr,
            int barsSinceEntry,
            out string reason)
        {
            reason = string.Empty;

            if (rates == null || rates.Length < 5 || atr <= 0)
                return false;

            symbol = symbol.ToUpperInvariant();

            // =============================
            // 🔥 PARAMETERS (per asset)
            // =============================
            int maxStallBars =
                IsGold(symbol) ? 4 :
                IsIndex(symbol) ? 3 :
                IsCrypto(symbol) ? 5 :
                IsFx(symbol) ? 6 :
                4;

            double minMoveAtr =
                IsGold(symbol) ? 0.6 :
                IsIndex(symbol) ? 0.7 :
                IsCrypto(symbol) ? 0.8 :
                IsFx(symbol) ? 0.4 :
                0.6;

            double weakBodyThreshold = 0.4; // candle weakness

            // =============================
            // 🔥 MOVE IN FAVOR
            // =============================
            double moveInFavor =
                side == TradeAction.EnterBuy
                    ? (currentPrice - entryPrice) / atr
                    : (entryPrice - currentPrice) / atr;

            // =============================
            // 🔥 STALL CONDITION 1
            // No movement after X bars
            // =============================
            if (barsSinceEntry >= maxStallBars && moveInFavor < minMoveAtr)
            {
                reason = $"STALL EXIT: no progress | bars={barsSinceEntry}, moveATR={moveInFavor:F2}";
                return true;
            }

            // =============================
            // 🔥 STALL CONDITION 2
            // Weak recent candles (no conviction)
            // =============================
            int weakCount = 0;

            for (int i = 1; i <= 3; i++)
            {
                var c = rates[^i];
                double localbody = Math.Abs(c.close - c.open);
                double range = c.high - c.low;

                if (range <= 0)
                    continue;

                double bodyRatio = localbody / range;

                if (bodyRatio < weakBodyThreshold)
                    weakCount++;
            }

            if (weakCount >= 3 && moveInFavor < minMoveAtr)
            {
                reason = $"STALL EXIT: weak candles | weakCount={weakCount}, moveATR={moveInFavor:F2}";
                return true;
            }

            // =============================
            // 🔥 STALL CONDITION 3
            // Loss of structure (optional light version)
            // =============================
            var last = rates[^1];

            bool againstPosition =
                side == TradeAction.EnterBuy
                    ? last.close < last.open // bearish candle
                    : last.close > last.open; // bullish candle

            double body = Math.Abs(last.close - last.open);
            double bodyAtr = body / atr;

            if (againstPosition && bodyAtr > 0.8 && moveInFavor < minMoveAtr)
            {
                reason = $"STALL EXIT: strong opposite candle | bodyATR={bodyAtr:F2}";
                return true;
            }

            return false;
        }

        // =============================
        // HELPERS
        // =============================

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
                   symbol.Contains("SPX") ||
                   symbol.Contains(".CASH");
        }

        private static bool IsFx(string symbol)
        {
            return !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);
        }
    }
}
