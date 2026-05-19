using MetaTrader.Data.Entities;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class MarketConditionEngine
    {
        public static MarketConditionSnapshot Calculate(
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow,
            double atr)
        {
            if (rates == null ||
                rates.Length < 5 ||
                emaFast == null ||
                emaFast.Length < 5 ||
                emaSlow == null ||
                emaSlow.Length < 5)
            {
                return null;
            }

            var last = rates[^1];
            var prev = rates[^2];

            double ema9 = emaFast[^1];
            double prevEma9 = emaFast[^2];

            double ema21 = emaSlow[^1];
            double prevEma21 = emaSlow[^2];

            double candleBody =
                Math.Abs(last.close - last.open);

            double candleRange =
                last.high - last.low;

            double bodyRatio =
                candleRange <= 0
                    ? 0
                    : candleBody / candleRange;

            double rangeAtr =
                atr <= 0
                    ? 0
                    : candleRange / atr;

            double ema9Slope =
                ema9 - prevEma9;

            double ema21Slope =
                ema21 - prevEma21;

            double emaSpread =
                Math.Abs(ema9 - ema21);

            bool trendUp =
                ema9 > ema21 &&
                ema9Slope > 0;

            bool trendDown =
                ema9 < ema21 &&
                ema9Slope < 0;

            bool emaCompression =
                emaSpread < atr * 0.35;

            bool slopeWeakening =
                Math.Abs(ema9Slope) < atr * 0.10;

            return new MarketConditionSnapshot
            {
                BodyRatio = bodyRatio,
                RangeAtr = rangeAtr,

                Ema9 = ema9,
                PrevEma9 = prevEma9,

                Ema21 = ema21,
                PrevEma21 = prevEma21,

                Ema9Slope = ema9Slope,
                Ema21Slope = ema21Slope,

                EmaSpread = emaSpread,

                TrendUp = trendUp,
                TrendDown = trendDown,

                LastClose = last.close,
                PrevClose = prev.close,

                CandleRange = candleRange,
                CandleBody = candleBody,

                EmaCompression = emaCompression,
                SlopeWeakening = slopeWeakening
            };
        }
    }
}
