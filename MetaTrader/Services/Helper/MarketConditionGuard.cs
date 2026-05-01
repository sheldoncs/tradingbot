using MetaTrader.Data.Entities;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class MarketConditionGuard
    {
        public static MarketCondition Evaluate(
            string symbol,
            MqlRates[] rates,
            double currentPrice,
            double ema,
            double atr,
            out string reason)
        {
            reason = string.Empty;

            if (rates == null || rates.Length < 4 || atr <= 0)
            {
                reason = "invalid data";
                return MarketCondition.Unknown;
            }

            var profile = SymbolProfiles.Get(symbol);

            // --- Weak structure ---
            double body1 = Math.Abs(rates[^2].close - rates[^2].open);
            double body2 = Math.Abs(rates[^3].close - rates[^3].open);

            bool isWeak =
                body1 < atr * profile.WeakStructureThreshold &&
                body2 < atr * profile.WeakStructureThreshold;

            // --- Extension ---
            double distance = Math.Abs(currentPrice - ema);
            bool isExtended = distance > atr * profile.ExtensionFactor;

            // --- Classification ---
            if (isWeak && !isExtended)
            {
                reason = "compression";
                return MarketCondition.Compression;
            }

            if (!isWeak && !isExtended)
            {
                reason = "expansion";
                return MarketCondition.Expansion;
            }

            if (!isWeak && isExtended)
            {
                reason = "extended trend";
                return MarketCondition.ExtendedTrend;
            }

            if (isWeak && isExtended)
            {
                reason = "exhaustion";
                return MarketCondition.Exhaustion;
            }

            return MarketCondition.Unknown;
        }
    }
}
