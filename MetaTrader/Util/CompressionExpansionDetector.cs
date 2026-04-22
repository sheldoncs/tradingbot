using MtApi5;

namespace MetaTrader.Util
{
    public static class CompressionExpansionDetector
    {
        public static bool IsCompressionBreakout(
            MqlRates[] rates,
            int compressionLookback = 6,
            int rangeLookback = 3,
            double expansionMultiplier = 1.35)
        {
            if (rates == null || rates.Length < compressionLookback + rangeLookback + 2)
                return false;

            // Use LAST CLOSED candle
            var last = rates[^2];

            // ----------------------------------------------------
            // 1️⃣ Compression check (small ranges)
            // ----------------------------------------------------
            double avgCompressionRange = 0;

            for (int i = rates.Length - compressionLookback - 2; i < rates.Length - 2; i++)
                avgCompressionRange += rates[i].high - rates[i].low;

            avgCompressionRange /= compressionLookback;

            // ----------------------------------------------------
            // 2️⃣ Recent average range
            // ----------------------------------------------------
            double recentRangeAvg = 0;

            for (int i = rates.Length - rangeLookback - 2; i < rates.Length - 2; i++)
                recentRangeAvg += rates[i].high - rates[i].low;

            recentRangeAvg /= rangeLookback;

            double lastRange = last.high - last.low;

            // ----------------------------------------------------
            // 3️⃣ Expansion confirmation
            // ----------------------------------------------------
            bool compression = avgCompressionRange < recentRangeAvg * 0.85;
            bool expansion = lastRange > recentRangeAvg * expansionMultiplier;

            return compression && expansion;
        }
    }

}
