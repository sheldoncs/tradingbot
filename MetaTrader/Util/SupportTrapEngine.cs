using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class SupportTrapEngine
    {
        /// <summary>
        /// Detects whether price is sitting on a support trap zone.
        /// Blocks SELL entries to avoid liquidity sweeps.
        /// </summary>
        public static bool IsSupportTrap(
            MqlRates[] rates,
            double midPrice,
            double atr,
            string symbol)
        {
            if (rates.Length < 5)
                return false;

            MqlRates prev = rates[^2];
            MqlRates prev2 = rates[^3];

            double prevLow = prev.low;
            double prev2Low = prev2.low;

            // How close price is to support
            double supportDistance = Math.Abs(midPrice - prevLow);

            // Dynamic buffer — tighter for XAU, looser for FX
            double buffer = symbol == "XAUUSD"
                ? Math.Max(atr * 0.30, 0.50)
                : Math.Max(atr * 0.40, 0.0008);

            bool nearSupport = supportDistance <= buffer;

            // Wick-based liquidity cues
            double lastBody = Math.Abs(prev.close - prev.open);
            double lowerWick = Math.Min(prev.close, prev.open) - prev.low;
            double wickRatio = lowerWick / Math.Max(lastBody, atr * 0.1);

            bool largeWickIntoSupport = wickRatio > 0.60;

            // Structure tightening (compressed candles)
            bool compression =
                Math.Abs(prev.close - prev.open) < atr * 0.20 &&
                Math.Abs(prev2.close - prev2.open) < atr * 0.20;

            bool trapCondition =
                nearSupport &&
                (largeWickIntoSupport || compression);

            if (trapCondition)
            {
                Console.WriteLine(
                    $"⚠️ Support Trap Detected: dist={supportDistance:F3} wickRatio={wickRatio:F2} buffer={buffer:F3}"
                );

                LogFile.Write(
                    $"⚠️ Support Trap: Price near support ({prevLow:F2}), wickRatio={wickRatio:F2}, compression={compression}"
                );
            }

            return trapCondition;
        }
    }

}
