using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;

namespace MetaTrader.Services
{
    public static class BreakRetestEntryEngine
    {
        public static int LookbackCandles = 20;
        public static double RetestToleranceFactor = 0.25; // ATR factor
        public static double RejectionBodyRatio = 0.5;     // Candle strength
        private static int breakTestCounter = 0;

        public static void retestCounter()
        {
            LogFile.Write($"Break test entries count: {breakTestCounter + 1}");
            breakTestCounter++;
        }
        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < LookbackCandles + 5)
                return false;

            // ============================
            // 1️⃣ Identify recent structure high/low
            // ============================

            double recentHigh = rates.Skip(rates.Length - LookbackCandles)
                                     .Max(r => r.high);

            double recentLow = rates.Skip(rates.Length - LookbackCandles)
                                    .Min(r => r.low);

            var last = rates[^1];
            var prev = rates[^2];

            double tolerance = atr * RetestToleranceFactor;

            // ============================
            // 2️⃣ Bearish Break → Retest
            // ============================

            bool brokeBelow = prev.close < recentLow;
            bool retestZone = Math.Abs(last.high - recentLow) <= tolerance;

            bool bearishRejection =
                last.close < last.open &&
                (last.high - Math.Max(last.open, last.close)) >
                (last.high - last.low) * RejectionBodyRatio;

            bool bearishTrend = emaFast[^1] < emaSlow[^1];

            if (brokeBelow && retestZone && bearishRejection && bearishTrend)
            {
                retestCounter();
                Console.WriteLine($"[{symbol}] BreakRetest SELL triggered");
                action = TradeAction.EnterSell;
                return true;
            }

            // ============================
            // 3️⃣ Bullish Break → Retest
            // ============================

            bool brokeAbove = prev.close > recentHigh;
            bool retestZoneBull = Math.Abs(last.low - recentHigh) <= tolerance;

            bool bullishRejection =
                last.close > last.open &&
                (Math.Min(last.open, last.close) - last.low) >
                (last.high - last.low) * RejectionBodyRatio;

            bool bullishTrend = emaFast[^1] > emaSlow[^1];

            if (brokeAbove && retestZoneBull && bullishRejection && bullishTrend)
            {
                retestCounter();
                Console.WriteLine($"[{symbol}] BreakRetest BUY triggered");
                action = TradeAction.EnterBuy;
                return true;
            }

            return false;
        }
    }
}
