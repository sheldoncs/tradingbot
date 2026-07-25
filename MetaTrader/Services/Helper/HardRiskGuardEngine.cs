using MetaTrader.Data.Entities;
using MetaTrader.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace MetaTrader.Services.Helper
{
    public static class HardRiskGuardEngine
    {
        // 🔥 CONFIG (YOU CAN TUNE THESE)
        private const double MAX_LOSS_PER_TRADE = 12;   // NEVER lose more than this
        private const double MAX_DAILY_LOSS = 20;       // STOP trading after this
        private const double DAILY_PROFIT_LOCK = 20;    // protect profit after this

        // 🔥 ENTRY GUARD (block new trades)
        public static bool CanEnter(double dailyPnL)
        {
            if (dailyPnL <= -MAX_DAILY_LOSS)
            {
                LogFile.Write("🛑 DAILY LOSS LIMIT HIT → BLOCKING NEW TRADES");
                return false;
            }

            return true;
        }
        
        // 🔥 MAIN PROTECTION ENGINE
        public static void Protect(
            List<TrackedTrade> openTrades,
            double dailyPnL, out bool isClosingTrade)
        {
            isClosingTrade = false;
            foreach (var t in openTrades.ToList())
            {
               
                // --------------------------------------------------
                // 🔴 1. HARD STOP PER TRADE
                // --------------------------------------------------
                if (t.ProfitUsd <= -MAX_LOSS_PER_TRADE)
                {
                    LogFile.Write($"🛑 HARD STOP LOSS HIT | {t.Symbol} | loss={t.ProfitUsd:F2}");
                    isClosingTrade = true;
                    continue;
                }

                // --------------------------------------------------
                // 🟢 2. DAILY LOSS PROTECTION
                // --------------------------------------------------
                if (dailyPnL <= -MAX_DAILY_LOSS)
                {
                    LogFile.Write($"🛑 DAILY LOSS EXIT | {t.Symbol} | dailyPnL={dailyPnL:F2}");
                    isClosingTrade = true;
                    continue;
                }

                // --------------------------------------------------
                // 🔒 3. DAILY PROFIT PROTECTION
                // --------------------------------------------------
                if (dailyPnL >= DAILY_PROFIT_LOCK)
                {
                    double protectLevel = dailyPnL - 10; // allow only $10 giveback

                    if (t.ProfitUsd < protectLevel)
                    {
                        LogFile.Write($"🔒 DAILY PROFIT PROTECT | {t.Symbol} | profit={t.ProfitUsd:F2}");
                        isClosingTrade = true;
                        continue;
                    }
                }

                // --------------------------------------------------
                // 💥 4. VIOLENT REVERSAL PROTECTION (GOLD KILLER)
                // --------------------------------------------------
                if (t.LastCandleBodyUsd > 8 && t.ProfitUsd > 5)
                {
                    LogFile.Write($"💥 VOLATILITY EXIT | {t.Symbol}");
                    isClosingTrade = true;
                    continue;
                }
            }
        }
    }
}
