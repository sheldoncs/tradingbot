using System;
using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Util
{
    public static class DynamicThresholdEngine
    {
        public static double Calculate(
            double atrRaw,
            bool emaCompressed,
            MarketRegime regime,
            MomentumResult hybrid,
            bool isCrypto
        )
        {
            double threshold = 0.60;

            // -----------------------------
            // Volatility adjustment
            // -----------------------------
            if (atrRaw < 1.2)
                threshold -= 0.05;

            if (atrRaw > 2.5)
                threshold += 0.05;

            // -----------------------------
            // Compression penalty
            // -----------------------------
            if (emaCompressed)
                threshold += 0.05;

            // -----------------------------
            // Regime adjustment
            // -----------------------------
            if (regime == MarketRegime.Transition)
                threshold -= 0.05;

            if (regime == MarketRegime.Exhaustion)
                threshold += 0.10;

            // -----------------------------
            // Momentum bonuses
            // -----------------------------
            if (hybrid.Breakout)
                threshold -= 0.05;

            if (hybrid.Impulse)
                threshold -= 0.05;

            // -----------------------------
            // Asset class
            // -----------------------------
            if (isCrypto)
                threshold -= 0.05;

            return Math.Clamp(threshold, 0.45, 0.75);
        }
    }

}
