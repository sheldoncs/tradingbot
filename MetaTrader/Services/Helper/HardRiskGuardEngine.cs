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
        // ==================================================
        // 🔥 CONFIG
        // ==================================================

        private const double MAX_LOSS_PER_TRADE = 6.00;
        private const double MAX_DAILY_LOSS = 20;
        private const double DAILY_PROFIT_LOCK = 20;

        // ==================================================
        // 🔥 VOLATILITY / REVERSAL PROTECTION
        // ==================================================

        // Only consider volatility protection after
        // the trade has developed meaningful profit.
        private const double VOLATILITY_MIN_PEAK_PROFIT = 5.00;

        // Require an actual giveback from peak profit.
        //
        // Example:
        // MaxProfit = $8.50
        // Current   = $5.20
        // Giveback  = $3.30
        //
        // This can trigger.
        //
        // But:
        //
        // MaxProfit = $5.24
        // Current   = $5.24
        // Giveback  = $0
        //
        // This will NOT trigger.
        private const double VOLATILITY_GIVEBACK_USD = 3.00;

        // Large candle requirement.
        private const double LARGE_CANDLE_BODY_USD = 8.00;


        private static double GetMaxLossPerTrade(string symbol)
        {
            string s = symbol.ToUpperInvariant();

            if (s.Contains("XAU") || s.Contains("XAG"))
                return 6.00;

            if (s.Contains("BTC") || s.Contains("ETH"))
                return 7.00;

            if (s.Contains("BNB") ||
                s.Contains("SOL") ||
                s.Contains("XRP") ||
                s.Contains("ADA"))
                return 6.00;

            if (s.Contains("US100") ||
                s.Contains("NAS") ||
                s.Contains("USTEC") ||
                s.Contains(".CASH"))
                return 6.00;

            // FX
            return 5.00;
        }

        // ==================================================
        // 🔥 ENTRY GUARD
        // ==================================================

        public static bool CanEnter(double dailyPnL)
        {
            if (dailyPnL <= -MAX_DAILY_LOSS)
            {
                LogFile.Write(
                    "🛑 DAILY LOSS LIMIT HIT → BLOCKING NEW TRADES"
                );

                return false;
            }

            return true;
        }

        // ==================================================
        // 🔥 MAIN PROTECTION ENGINE
        // ==================================================

        public static void Protect(
            List<TrackedTrade> openTrades,
            double dailyPnL,
            out bool isClosingTrade)
        {
            isClosingTrade = false;

            if (openTrades == null || openTrades.Count == 0)
                return;

            foreach (var t in openTrades.ToList())
            {
                if (t == null)
                    continue;

                // ==================================================
                // 🔴 1. HARD STOP PER TRADE
                // ==================================================

                double maxLoss =
                     GetMaxLossPerTrade(t.Symbol);

                if (t.ProfitUsd <= -maxLoss)
                {
                    LogFile.Write(
                        $"🛑 HARD STOP LOSS HIT | " +
                        $"{t.Symbol} | " +
                        $"loss={t.ProfitUsd:F2} | " +
                        $"maxAllowed=-${maxLoss:F2}"
                    );

                    isClosingTrade = true;

                    continue;
                }

                // ==================================================
                // 🟢 2. DAILY LOSS PROTECTION
                // ==================================================

                if (dailyPnL <= -MAX_DAILY_LOSS)
                {
                    LogFile.Write(
                        $"🛑 DAILY LOSS EXIT | " +
                        $"{t.Symbol} | " +
                        $"dailyPnL={dailyPnL:F2}"
                    );

                    isClosingTrade = true;

                    continue;
                }

                // ==================================================
                // 🔒 3. DAILY PROFIT PROTECTION
                // ==================================================

                if (dailyPnL >= DAILY_PROFIT_LOCK)
                {
                    // Allow $10 giveback from current daily P/L.
                    double protectLevel =
                        dailyPnL - 10;

                    if (t.ProfitUsd < protectLevel)
                    {
                        LogFile.Write(
                            $"🔒 DAILY PROFIT PROTECT | " +
                            $"{t.Symbol} | " +
                            $"profit={t.ProfitUsd:F2}, " +
                            $"dailyPnL={dailyPnL:F2}, " +
                            $"protectLevel={protectLevel:F2}"
                        );

                        isClosingTrade = true;

                        continue;
                    }
                }

                // ==================================================
                // 💥 4. VIOLENT REVERSAL / VOLATILITY PROTECTION
                //
                // OLD BEHAVIOUR:
                //
                // large candle + profit > $5
                //         ↓
                // EXIT
                //
                // Problem:
                // A large candle IN FAVOR of the trade also exited.
                //
                //
                // NEW BEHAVIOUR:
                //
                // large candle
                //       +
                // meaningful peak profit
                //       +
                // meaningful GIVEBACK from peak
                //       ↓
                // EXIT
                //
                // This allows strong runners to continue.
                // ==================================================

                double peakProfit =
                    Math.Max(
                        t.MaxProfitUsd,
                        t.ProfitUsd
                    );

                double profitGiveback =
                    Math.Max(
                        0.0,
                        peakProfit - t.ProfitUsd
                    );

                bool largeVolatilityCandle =
                    t.LastCandleBodyUsd >=
                    LARGE_CANDLE_BODY_USD;

                bool hadMeaningfulProfit =
                    peakProfit >=
                    VOLATILITY_MIN_PEAK_PROFIT;

                bool meaningfulGiveback =
                    profitGiveback >=
                    VOLATILITY_GIVEBACK_USD;

                if (largeVolatilityCandle &&
                    hadMeaningfulProfit &&
                    meaningfulGiveback)
                {
                    LogFile.Write(
                        $"💥 VOLATILITY REVERSAL EXIT | " +
                        $"{t.Symbol} | " +
                        $"profit={t.ProfitUsd:F2}, " +
                        $"peak={peakProfit:F2}, " +
                        $"giveback={profitGiveback:F2}, " +
                        $"candleBodyUsd={t.LastCandleBodyUsd:F2}"
                    );

                    isClosingTrade = true;

                    continue;
                }

                // ==================================================
                // 🔎 OPTIONAL HOLD LOG
                //
                // Useful when a large candle occurs but the
                // trade is still running strongly.
                // ==================================================

                if (largeVolatilityCandle &&
                    hadMeaningfulProfit &&
                    !meaningfulGiveback)
                {
                    LogFile.Write(
                        $"🚀 VOLATILITY HOLD | " +
                        $"{t.Symbol} | " +
                        $"profit={t.ProfitUsd:F2}, " +
                        $"peak={peakProfit:F2}, " +
                        $"giveback={profitGiveback:F2}, " +
                        $"candleBodyUsd={t.LastCandleBodyUsd:F2}"
                    );
                }
            }
        }
    }
}
