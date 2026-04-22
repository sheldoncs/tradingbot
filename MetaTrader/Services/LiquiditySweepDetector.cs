using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class LiquiditySweepDetector
    {
        public static bool IsBearishSweep(MqlRates[] rates, int lookback = 5)
        {
            if (rates == null || rates.Length < lookback + 3)
                return false;

            var sweepCandle = rates[^2]; // closed candle

            double recentHigh = double.MinValue;

            for (int i = 3; i < 3 + lookback; i++)
                recentHigh = Math.Max(recentHigh, rates[^i].high);

            // broke above high but closed back below it
            bool brokeAbove = sweepCandle.high > recentHigh;
            bool closedBackInside = sweepCandle.close < recentHigh;

            return brokeAbove && closedBackInside;
        }

        public static bool IsBullishSweep(MqlRates[] rates, int lookback = 5)
        {
            if (rates == null || rates.Length < lookback + 3)
                return false;

            var sweepCandle = rates[^2];

            double recentLow = double.MaxValue;

            for (int i = 3; i < 3 + lookback; i++)
                recentLow = Math.Min(recentLow, rates[^i].low);

            bool brokeBelow = sweepCandle.low < recentLow;
            bool closedBackInside = sweepCandle.close > recentLow;

            return brokeBelow && closedBackInside;
        }
    }
}
