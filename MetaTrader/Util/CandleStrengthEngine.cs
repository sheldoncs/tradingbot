using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class CandleStrengthEngine
    {
        public static double ScoreCandle(MqlRates candle, double atr)
        {
            double body = Math.Abs(candle.close - candle.open);
            double range = candle.high - candle.low;

            double upperWick = candle.high - Math.Max(candle.close, candle.open);
            double lowerWick = Math.Min(candle.close, candle.open) - candle.low;

            double bodyScore = (body / atr) * 80.0;
            if (bodyScore > 80) bodyScore = 80;

            double wickImbalance = (lowerWick - upperWick) / Math.Max(range, atr * 0.2);
            double wickScore = wickImbalance * 20.0;

            double total = bodyScore + wickScore;

            return Math.Clamp(total, -100, 100);
        }
    }

}
