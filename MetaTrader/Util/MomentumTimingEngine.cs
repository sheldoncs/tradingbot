using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class MomentumTimingEngine
    {
        public static bool IsLateMomentumExhaustion(
            MqlRates[] rates1H,
            bool isBullTrend,
            bool isBearTrend,
            double atrRaw,
            bool emaCompressed,
            bool hybridStrong,
            bool isCrypto)   // 👈 ADD THIS
        {
            if (rates1H == null || rates1H.Length < 10)
                return false;

            // Compression is ALWAYS blocked
            if (emaCompressed)
                return true;

            if (!hybridStrong)
                return false;

            // -----------------------------
            // 🔧 Instrument-aware tuning
            // -----------------------------
            double lateMoveAtrMultiple = isCrypto ? 1.8 : 1.2;
            int maxContinuationCount = isCrypto ? 3 : 2;

            int continuationCount = CountContinuationCandles(
                rates1H, isBullTrend, isBearTrend, isCrypto);

            if (continuationCount >= maxContinuationCount)
                return true;

            int resetIndex = FindMostRecentResetIndex(
                rates1H,
                lookback: isCrypto ? 16 : 12,
                isCrypto);

            if (resetIndex == -1)
                return false;

            double moveFromReset = isBullTrend
                ? rates1H[^1].close - rates1H[resetIndex].low
                : rates1H[resetIndex].high - rates1H[^1].close;

            return moveFromReset >= atrRaw * lateMoveAtrMultiple;
        }

        // ---------------------------------------

        public static bool IsFirstValidContinuationCandle(
            MqlRates[] rates1H,
            bool isBullTrend,
            bool isBearTrend,
            bool isCrypto)   // 👈 ADD THIS
        {
            if (rates1H == null || rates1H.Length < 6)
                return false;

            var last = rates1H[^1];
            var prev = rates1H[^2];
            var prev2 = rates1H[^3];

            if (!IsContinuationCandle(last, isBullTrend, isBearTrend, isCrypto))
                return false;

            if (!IsResetCandle(prev, isCrypto))
                return false;

            // Crypto allows continuation after mild continuation
            if (!isCrypto && IsContinuationCandle(prev2, isBullTrend, isBearTrend, isCrypto))
                return false;

            return true;
        }

        // ---------------------------------------

        private static int CountContinuationCandles(
            MqlRates[] rates,
            bool bull,
            bool bear,
            bool isCrypto)
        {
            int count = 0;
            int start = Math.Max(0, rates.Length - (isCrypto ? 7 : 5));

            for (int i = start; i < rates.Length; i++)
            {
                if (IsContinuationCandle(rates[i], bull, bear, isCrypto))
                    count++;
            }

            return count;
        }

        private static bool IsContinuationCandle(
            MqlRates c,
            bool bull,
            bool bear,
            bool isCrypto)
        {
            double body = Math.Abs(c.close - c.open);
            double range = Math.Max(1e-6, c.high - c.low);
            double bodyRatio = body / range;

            double minBodyRatio = isCrypto ? 0.35 : 0.45;

            if (bodyRatio < minBodyRatio)
                return false;

            if (bull && c.close > c.open)
                return true;

            if (bear && c.close < c.open)
                return true;

            return false;
        }

        private static bool IsResetCandle(
            MqlRates c,
            bool isCrypto)
        {
            double body = Math.Abs(c.close - c.open);
            double range = Math.Max(1e-6, c.high - c.low);
            double bodyRatio = body / range;

            double resetThreshold = isCrypto ? 0.55 : 0.45;

            return bodyRatio <= resetThreshold;
        }

        private static int FindMostRecentResetIndex(
            MqlRates[] rates,
            int lookback,
            bool isCrypto)
        {
            int end = rates.Length - 2;
            int start = Math.Max(0, end - lookback);

            for (int i = end; i >= start; i--)
            {
                if (IsResetCandle(rates[i], isCrypto))
                    return i;
            }

            return -1;
        }
    }

}
