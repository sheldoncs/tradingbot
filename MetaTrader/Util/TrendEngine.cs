using MtApi5;
using System;

namespace MetaTrader.Util
{
    public enum TrendDirection
    {
        None = 0,
        Bullish = 1,
        Bearish = -1
    }

    public readonly struct TrendContext
    {
        public TrendDirection Direction { get; }
        public bool IsChoppy { get; }
        public bool BullishBreakout { get; }
        public bool BearishBreakout { get; }
        public bool IsImpulse { get; }
        public string Reason { get; }
        public bool HasNewStructure =>
        BullishBreakout || BearishBreakout;
        public TrendContext(
            TrendDirection direction,
            bool isChoppy,
            bool bullishBreakout,
            bool bearishBreakout,
            bool isImpulse,
            string reason)
        {
            Direction = direction;
            IsChoppy = isChoppy;
            BullishBreakout = bullishBreakout;
            BearishBreakout = bearishBreakout;
            IsImpulse = isImpulse;
            Reason = reason;
        }

        public bool IsEmaCompressed(double[] emaFast, double[] emaSlow, double atrRaw)
        {

            if (emaFast == null || emaSlow == null || emaFast.Length < 2 || emaSlow.Length < 2)
                return false;

            double distance = Math.Abs(emaFast[^1] - emaSlow[^1]);

            // Compression when EMAs are very close relative to ATR
            return distance < atrRaw * 0.15; // tuneable (XAU: 0.10–0.20 works well)
        }
    }

    public static class TrendEngine
    {
        public static TrendContext Analyze(
            double[] emaFast,
            double[] emaSlow,
            MqlRates[] rates,
            double atr)
        {
            // Safety check
            if (emaFast == null || emaSlow == null || rates == null ||
                emaFast.Length < 3 || emaSlow.Length < 3 || rates.Length < 3)
            {
                return new TrendContext(
                    TrendDirection.None,
                    isChoppy: true,
                    bullishBreakout: false,
                    bearishBreakout: false,
                    isImpulse: false,
                    reason: "Not enough candles/EMA data.");
            }

            var last = rates[^1];
            var prev = rates[^2];

            double lastBody = Math.Abs(last.close - last.open);

            // ===== PARAMETERS =====
            double buffer = Math.Max(atr * 0.05, 0.50);  // Early breakout buffer for XAUUSD
            double smallBodyThreshold = atr * 0.20;
            double impulseBodyThreshold = atr * 0.70;

            // ===== EMA TREND =====
            bool emaBull = emaFast[^1] > emaSlow[^1];
            bool emaBear = emaFast[^1] < emaSlow[^1];

            // Early structure detection
            bool lowerHigh = last.high < prev.high;
            bool lowerLow = last.low < prev.low;
            bool higherHigh = last.high > prev.high;
            bool higherLow = last.low > prev.low;

            bool earlyBear = lowerHigh && lowerLow;
            bool earlyBull = higherHigh && higherLow;

            TrendDirection direction =
                (emaBear || earlyBear) ? TrendDirection.Bearish :
                (emaBull || earlyBull) ? TrendDirection.Bullish :
                TrendDirection.None;

            // ===== BREAKOUT =====
            bool bullishBreakout = last.close > prev.high + buffer;
            bool bearishBreakout = last.close < prev.low - buffer;

            // ===== IMPULSE =====
            bool impulse =
                lastBody >= impulseBodyThreshold &&
                (
                    (direction == TrendDirection.Bearish && last.close <= last.low + atr * 0.15) ||
                    (direction == TrendDirection.Bullish && last.close >= last.high - atr * 0.15)
                );

            // ===== CHOPPY =====
            double emaDistance = Math.Abs(emaFast[^1] - emaSlow[^1]);
            bool emaFlat = emaDistance < atr * 0.02;

            bool lowVol = lastBody < smallBodyThreshold &&
                          (prev == null || Math.Abs(prev.close - prev.open) < smallBodyThreshold);

            bool isChoppy = emaFlat && lowVol && !impulse;

            return new TrendContext(
                direction,
                isChoppy,
                bullishBreakout,
                bearishBreakout,
                impulse,
                reason: "TrendEngine computed trend.");
        }

        



    }



}
