using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class MomentumEvaluator
    {
        public static bool IsStrong(
            MqlRates candle,
            double atr,
            double minRangeAtr,
            double minBodyRatio)
        {
            double range = candle.high - candle.low;
            if (range <= 0 || atr <= 0)
                return false;

            double body = Math.Abs(candle.close - candle.open);
            double ratio = body / range;

            return range > atr * minRangeAtr &&
                   ratio > minBodyRatio;
        }
    }
}
