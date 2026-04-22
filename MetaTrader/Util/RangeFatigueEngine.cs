using MtApi5;
using System;
using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Util
{
    public static class RangeFatigueEngine
    {
        public static bool IsRangeFatigued(
            MqlRates[] rates,
            bool emaCompressed,
            MomentumResult hybrid,
            bool bullishAcceptance,
            bool bearishAcceptance,
            double atrRaw,
            int lookbackCandles = 6,
            double maxRangeAtrFraction = 0.35
        )
        {
            // ----------------------------------------------------------
            // 1️⃣ EMA compression must be present
            // ----------------------------------------------------------
            if (!emaCompressed)
                return false;

            // ----------------------------------------------------------
            // 2️⃣ Momentum fired but NO acceptance
            // ----------------------------------------------------------
            bool momentumWithoutAcceptance =
                hybrid.Strong &&
                !bullishAcceptance &&
                !bearishAcceptance;

            if (!momentumWithoutAcceptance)
                return false;

            // ----------------------------------------------------------
            // 3️⃣ Price stuck in tight range
            // ----------------------------------------------------------
            double high = rates[^1].high;
            double low = rates[^1].low;

            for (int i = 2; i <= lookbackCandles; i++)
            {
                high = Math.Max(high, rates[^i].high);
                low = Math.Min(low, rates[^i].low);
            }

            double range = high - low;

            bool tightRange = range < atrRaw * maxRangeAtrFraction;

            // ----------------------------------------------------------
            // FINAL DECISION
            // ----------------------------------------------------------
            return tightRange;
        }
    }

}
