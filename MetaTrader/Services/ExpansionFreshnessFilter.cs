using MetaTrader.Enums;
using MtApi5;

namespace MetaTrader.Services
{
    public static class ExpansionFreshnessFilter
    {
        public static bool IsFresh(
            MqlRates[] rates,
            double atr,
            VelocityProfile profile)
        {
            if (rates == null || rates.Length < 3 || atr <= 0)
                return true; // fail-open

            var cfg = ExpansionFreshnessProfiles.For(profile);

            var c1 = rates[1]; // last closed
            var c2 = rates[2]; // previous closed

            double range1 = c1.high - c1.low;
            double range2 = c2.high - c2.low;

            if (range1 >= atr * cfg.MaxSingleCandleAtr)
                return false;

            if ((range1 + range2) >= atr * cfg.MaxTwoCandleAtr)
                return false;

            return true;
        }
    }
}
