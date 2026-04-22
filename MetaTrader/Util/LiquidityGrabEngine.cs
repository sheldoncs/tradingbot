using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class LiquidityGrabEngine
    {
        public static bool IsBearishLiquidityGrab(
            MqlRates last,
            double recentHigh,
            bool strongMomentum)
        {
            double body = Math.Abs(last.close - last.open);
            double upperWick = last.high - Math.Max(last.close, last.open);

            bool longUpperWick = upperWick > body * 1.5;

            bool failedBreak =
                last.high > recentHigh &&
                last.close < recentHigh;

            return failedBreak && longUpperWick && !strongMomentum;
        }

        public static bool IsBullishLiquidityGrab(
            MqlRates last,
            double recentLow,
            bool strongMomentum)
        {
            double body = Math.Abs(last.close - last.open);
            double lowerWick = Math.Min(last.close, last.open) - last.low;

            bool longLowerWick = lowerWick > body * 1.5;

            bool failedBreak =
                last.low < recentLow &&
                last.close > recentLow;

            return failedBreak && longLowerWick && !strongMomentum;
        }
    }


}
