using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class DynamicMomentum
    {
        // ----------------------------------------------------------
        // 🔵 Detect Dynamic Bullish Override
        // ----------------------------------------------------------
        public static bool IsBullishOverride(MqlRates last, MqlRates prev, double atrRaw)
        {
            double lastBody = Math.Abs(last.close - last.open);
            double lowerWick = Math.Min(last.close, last.open) - last.low;

            if (atrRaw <= 0) return false;

            double wickRatio = lowerWick / Math.Max(lastBody, atrRaw * 0.1);
            double bodyRatio = lastBody / Math.Max(atrRaw, 0.0001);

            // dynamic threshold
            double threshold = 0.50 + (atrRaw * 0.03);

            bool bullishClose = last.close > last.open;
            bool failedBullBreak = last.close <= prev.high;

            bool condition =
                wickRatio >= threshold &&
                bullishClose &&
                failedBullBreak;

            if (condition)
            {
                Console.WriteLine($"🔵 Bullish momentum override: wickRatio={wickRatio:F2} bodyRatio={bodyRatio:F2}");
                LogFile.Write($"🔵 Bullish momentum override: wickRatio={wickRatio:F2} bodyRatio={bodyRatio:F2}");
            }

            return condition;
        }

        // ----------------------------------------------------------
        // 🔴 Detect Dynamic Bearish Override
        // ----------------------------------------------------------
        public static bool IsBearishOverride(MqlRates last, MqlRates prev, double atrRaw)
        {
            double lastBody = Math.Abs(last.close - last.open);
            double upperWick = last.high - Math.Max(last.close, last.open);

            if (atrRaw <= 0) return false;

            double wickRatio = upperWick / Math.Max(lastBody, atrRaw * 0.1);
            double bodyRatio = lastBody / Math.Max(atrRaw, 0.0001);

            double threshold = 0.50 + (atrRaw * 0.03);

            bool bearishClose = last.close < last.open;
            bool failedBearBreak = last.close >= prev.close;

            bool condition =
                wickRatio >= threshold &&
                bearishClose &&
                failedBearBreak;

            if (condition)
            {
                Console.WriteLine($"🔴 Bearish momentum override: wickRatio={wickRatio:F2} bodyRatio={bodyRatio:F2}");
                LogFile.Write($"🔴 Bearish momentum override: wickRatio={wickRatio:F2} bodyRatio={bodyRatio:F2}");
            }

            return condition;
        }
    }

}
