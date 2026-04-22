using MetaTrader.Data.Entities;
using MetaTrader.Util;
using System;

namespace MetaTrader.Services
{
    public static class ProfitProtectionEngine
    {
        private const double BASE_BUFFER_USD = 0.20;

        public static bool ShouldCloseTrade(TrackedTrade trade, string symbol)
        {
            if (trade == null)
                return false;

            // Track peak profit
            trade.MaxProfitUsd = Math.Max(trade.MaxProfitUsd, trade.ProfitUsd);

            // ----------------------------------------------------------
            // 🔥 DETECT ASSET TYPE
            // ----------------------------------------------------------
            bool isGold = symbol.Contains("XAU");
            bool isIndex = symbol.Contains("US100") || symbol.Contains("NAS");
            bool isFx = symbol.Length == 6 && symbol.EndsWith("USD") && !isGold && !isIndex;
            bool isCrypto = !isGold && !isIndex && !isFx;

            // ----------------------------------------------------------
            // 🔥 DYNAMIC PROFIT FLOOR + BUFFER
            // ----------------------------------------------------------
            double profitFloor = 3.0;
            double buffer = BASE_BUFFER_USD;

            if (isGold)
            {
                profitFloor = 5.0;
                buffer = 0.40;
            }
            else if (isIndex)
            {
                profitFloor = 6.0;
                buffer = 0.60;
            }
            else if (isCrypto)
            {
                profitFloor = 5.0;
                buffer = 0.50;
            }
            else if (isFx)
            {
                profitFloor = 2.5;
                buffer = 0.20;
            }

            // ----------------------------------------------------------
            // 🔥 NEW: MINIMUM HOLD PHASE (CRITICAL FIX)
            // ----------------------------------------------------------
            double activationLevel = profitFloor * 1.5;

            // Do NOT protect too early — let trade breathe
            if (trade.MaxProfitUsd < activationLevel)
                return false;

            // ----------------------------------------------------------
            // 🔥 STAGE 1 — INITIAL TRAILING (DELAYED)
            // ----------------------------------------------------------
            if (trade.MaxProfitUsd >= activationLevel &&
                trade.MaxProfitUsd < profitFloor * 3)
            {
                double dynamicBuffer = buffer * 1.8; // wider buffer

                if (trade.ProfitUsd <= trade.MaxProfitUsd - dynamicBuffer)
                {
                    LogFile.Write($"[{symbol}] ProfitProtection: delayed initial trailing");
                    return true;
                }
            }

            // ----------------------------------------------------------
            // 🔥 STAGE 2 — MEDIUM PROFITS (TREND CAPTURE)
            // ----------------------------------------------------------
            if (trade.MaxProfitUsd >= profitFloor * 3 &&
                trade.MaxProfitUsd < profitFloor * 5)
            {
                double dynamicBuffer = buffer * 2.5;

                if (trade.ProfitUsd <= trade.MaxProfitUsd - dynamicBuffer)
                {
                    LogFile.Write($"[{symbol}] ProfitProtection: trend trailing");
                    return true;
                }
            }

            // ----------------------------------------------------------
            // 🔥 STAGE 3 — LARGE PROFITS (LET IT RUN)
            // ----------------------------------------------------------
            if (trade.MaxProfitUsd >= profitFloor * 5)
            {
                double dynamicBuffer = buffer * 3.5;

                if (trade.ProfitUsd <= trade.MaxProfitUsd - dynamicBuffer)
                {
                    LogFile.Write($"[{symbol}] ProfitProtection: runner trailing");
                    return true;
                }
            }

            return false;
        }
    }

}
