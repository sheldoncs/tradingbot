using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class AcceptanceEngine
    {
        public static bool IsBullishAcceptance(
            MqlRates prev,
            MqlRates last,
            double level,
            double atrRaw)
        {
            double prevBody = Math.Abs(prev.close - prev.open);
            double lastBody = Math.Abs(last.close - last.open);

            bool prevClosedAbove = prev.close > level;
            bool lastHeldAbove = last.low > level;

            bool strongBody =
                prevBody > atrRaw * 0.25 &&
                lastBody > atrRaw * 0.15;

            return prevClosedAbove && lastHeldAbove && strongBody;
        }

        public static bool IsBearishAcceptance(
            MqlRates prev,
            MqlRates last,
            double level,
            double atrRaw)
        {
            double prevBody = Math.Abs(prev.close - prev.open);
            double lastBody = Math.Abs(last.close - last.open);

            bool prevClosedBelow = prev.close < level;
            bool lastHeldBelow = last.high < level;

            bool strongBody =
                prevBody > atrRaw * 0.25 &&
                lastBody > atrRaw * 0.15;

            return prevClosedBelow && lastHeldBelow && strongBody;
        }
    }

}
