using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class DynamicExhaustionOverride
    {
        public static bool ShouldOverrideBullishExhaustion(
            MqlRates last,
            MqlRates current,
            double atr)
        {
            // Must be bullish continuation
            bool curBull = current.close > current.open;

            // Candle momentum
            double curBody = Math.Abs(current.close - current.open);
            bool curHasMomentum = curBody >= atr * 0.15;

            // Strong close — no new exhaustion 
            double curUpperWick = current.high - current.close;
            bool curStrongClose = curUpperWick <= atr * 0.10;

            // Override condition
            return curBull && curHasMomentum && curStrongClose;
        }


        public static bool ShouldOverrideBearishExhaustion(
            MqlRates last,
            MqlRates current,
            double atr)
        {
            bool curBear = current.close < current.open;

            double curBody = Math.Abs(current.close - current.open);
            bool curHasMomentum = curBody >= atr * 0.15;

            double curLowerWick = current.close - current.low;
            bool curStrongClose = curLowerWick <= atr * 0.10;

            return curBear && curHasMomentum && curStrongClose;
        }
    }

}
