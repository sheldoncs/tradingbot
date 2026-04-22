using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class ResistanceTrapEngine
    {
        /// <summary>
        /// Detects a resistance trap zone where BUY trades frequently get reversed.
        /// </summary>
        public static bool IsResistanceTrap(
            MqlRates[] rates,
            double midPrice,
            double atr,
            string symbol)
        {
            if (rates.Length < 5)
                return false;

            MqlRates prev = rates[^2];
            MqlRates prev2 = rates[^3];

            double prevHigh = prev.high;
            double prev2High = prev2.high;

            // Distance to resistance
            double resistanceDistance = Math.Abs(prevHigh - midPrice);

            // Dynamic buffer tuned for each symbol
            double buffer = symbol == "XAUUSD"
                ? Math.Max(atr * 0.30, 0.50)      // Gold = bigger noise
                : Math.Max(atr * 0.40, 0.0008);  // Forex = pip-sized noise

            bool nearResistance = resistanceDistance <= buffer;

            // Wick-based liquidity sweep signals
            double lastBody = Math.Abs(prev.close - prev.open);
            double upperWick = prev.high - Math.Max(prev.close, prev.open);
            double wickRatio = upperWick / Math.Max(lastBody, atr * 0.1);

            bool largeUpperWick = wickRatio > 0.60;

            // Compression at resistance (buyers failing to push through)
            bool compression =
                Math.Abs(prev.close - prev.open) < atr * 0.20 &&
                Math.Abs(prev2.close - prev2.open) < atr * 0.20;

            bool trapCondition =
                nearResistance &&
                (largeUpperWick || compression);

            if (trapCondition)
            {
                Console.WriteLine(
                    $"⚠️ Resistance Trap Detected: dist={resistanceDistance:F3} wickRatio={wickRatio:F2} buffer={buffer:F3}"
                );

                LogFile.Write(
                    $"⚠️ Resistance Trap: Price near resistance ({prevHigh:F2}), wickRatio={wickRatio:F2}, compression={compression}"
                );
            }

            return trapCondition;
        }
    }

}
