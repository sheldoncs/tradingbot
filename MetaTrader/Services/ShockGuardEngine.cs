using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class ShockGuardEngine
    {
        public static bool DetectShock(
            MqlRates[] rates,
            double atr,
            int lookbackCandles,
            double shockMultiplier,
            double shockBodyRatio)
        {
            if (rates == null || rates.Length < lookbackCandles + 1)
                return false;

            // Check live candle
            var current = rates[0];
            if (IsShockCandle(current, atr, shockMultiplier, shockBodyRatio))
                return true;

            // Check recent candles
            for (int i = 1; i <= lookbackCandles; i++)
            {
                if (IsShockCandle(rates[i], atr, shockMultiplier, shockBodyRatio))
                    return true;
            }

            return false;
        }

        private static bool IsShockCandle(
            MqlRates candle,
            double atr,
            double shockMultiplier,
            double shockBodyRatio)
        {
            double range = candle.high - candle.low;
            double body = Math.Abs(candle.close - candle.open);

            return
                range >= atr * shockMultiplier &&
                (body / range) >= shockBodyRatio;
        }
    }

}
