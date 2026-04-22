using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class ImpulseQualityFilter
    {
        // Tuned for Gold / swing-style entries
        private const double MIN_ATR_EXPANSION = 1.05;   // ATR must be expanding
        private const double MIN_BODY_ATR_RATIO = 0.35; // Candle body vs ATR
        private const double MIN_EMA_DISTANCE_ATR = 0.25; // Separation from EMA

        /// <summary>
        /// Returns TRUE if entry is allowed.
        /// Returns FALSE if market energy is too weak / drifting.
        /// </summary>
        public static bool IsImpulseStrongEnough(
            MqlRates[] rates,
            double atr,
            double emaFast,
            double emaSlow)
        {
            if (rates == null || rates.Length < 5 || atr <= 0)
                return false;

            var last = rates[^1];
            var prev = rates[^2];

            // 1️⃣ ATR expansion check
            double prevRange = prev.high - prev.low;
            double currRange = last.high - last.low;

            bool atrExpanding = currRange >= prevRange * MIN_ATR_EXPANSION;

            // 2️⃣ Candle body strength vs ATR
            double bodySize = Math.Abs(last.close - last.open);
            bool bodyStrongEnough = (bodySize / atr) >= MIN_BODY_ATR_RATIO;

            // 3️⃣ Price separation from EMA (avoid EMA hugging)
            double price = last.close;
            double emaDistance = Math.Abs(price - emaFast);

            bool separatedFromEma = (emaDistance / atr) >= MIN_EMA_DISTANCE_ATR;

            // ✅ Allow entry ONLY if energy is real
            return atrExpanding && bodyStrongEnough && separatedFromEma;
        }
    }

}
