using MtApi5;

namespace MetaTrader.Util
{
    public static class LowVolatilityFilter
    {
        // Gold-safe defaults
        private const double MIN_ATR_RATIO = 0.35;   // 35% of normal ATR
        private const int LOOKBACK = 5;              // last 5 candles

        public static bool ShouldBlockTrade(MqlRates[] rates1H, double atr)
        {
            if (rates1H == null || rates1H.Length < LOOKBACK + 1 || atr <= 0)
                return true;

            double avgRange = 0.0;

            for (int i = rates1H.Length - LOOKBACK; i < rates1H.Length; i++)
            {
                avgRange += (rates1H[i].high - rates1H[i].low);
            }

            avgRange /= LOOKBACK;

            // If recent candles are too small relative to ATR → block
            return avgRange < (atr * MIN_ATR_RATIO);
        }
    }

}
