using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class MomentumScoringEngine
    {
        public static double LastMomentumScore = 0;

        /// <summary>
        /// Calculates dynamic momentum score using body, wick balance,
        /// close placement, EMA slope, ATR normalization.
        /// </summary>
        public static double ScoreMomentum(
            MqlRates last,
            MqlRates prev,
            double[] emaFast,
            double atrRaw)
        {
            double lastBody = Math.Abs(last.close - last.open);

            double upperWick = last.high - Math.Max(last.close, last.open);
            double lowerWick = Math.Min(last.close, last.open) - last.low;

            double bodyScore = (lastBody / atrRaw) * 100.0;

            // wick score: large wicks reduce momentum
            double wickScore = 100.0 - ((upperWick + lowerWick) / atrRaw) * 50.0;
            if (wickScore < 0) wickScore = 0;

            // close position score
            double candleRange = last.high - last.low;
            double closeScore = 0;
            if (candleRange > 0)
                closeScore = Math.Abs((last.close - last.open) / candleRange) * 100.0;

            // EMA slope component
            double emaSlope = emaFast[^1] - emaFast[^2];
            double slopeScore = (emaSlope / atrRaw) * 120.0;
            slopeScore = Math.Clamp(slopeScore, -50, 100);

            // final weighted momentum score
            double momentumScore =
                (bodyScore * 0.45) +
                (wickScore * 0.25) +
                (closeScore * 0.20) +
                (slopeScore * 0.10);

            // Clamp for safety
            momentumScore = Math.Clamp(momentumScore, 0, 100);

            Console.WriteLine($"MomentumScore(calc) = {momentumScore:F2}");
            LogFile.Write($"MomentumScore(calc) = {momentumScore:F2}");

            LastMomentumScore = momentumScore;
            return momentumScore;
        }

        /// <summary>
        /// Creates a dynamic threshold based on ATR.
        /// Higher ATR → slightly higher threshold requirement.
        /// </summary>
        public static double DynamicThreshold(double atrRaw)
        {
            double threshold = 40 + (atrRaw * 1.2);

            if (threshold < 35) threshold = 35;
            if (threshold > 70) threshold = 70;

            Console.WriteLine($"DynamicThreshold = {threshold:F2}");
            LogFile.Write($"DynamicThreshold = {threshold:F2}");

            return threshold;
        }
    }

}
