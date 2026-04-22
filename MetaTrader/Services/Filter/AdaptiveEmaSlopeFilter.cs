using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Filter
{
    public static class AdaptiveEmaSlopeFilter
    {
        private class SlopeProfile
        {
            public double MinSlopeAtrFactor { get; set; }
            public double RelaxedSlopeAtrFactor { get; set; }
        }

        // -----------------------------------------
        // PROFILE PER ASSET
        // -----------------------------------------
        private static SlopeProfile GetProfile(string symbol)
        {
            if (IsGold(symbol))
            {
                return new SlopeProfile
                {
                    MinSlopeAtrFactor = 0.12,
                    RelaxedSlopeAtrFactor = 0.05
                };
            }

            if (IsIndex(symbol))
            {
                return new SlopeProfile
                {
                    MinSlopeAtrFactor = 0.10,
                    RelaxedSlopeAtrFactor = 0.04
                };
            }

            if (IsCrypto(symbol))
            {
                return new SlopeProfile
                {
                    MinSlopeAtrFactor = 0.08,
                    RelaxedSlopeAtrFactor = 0.03
                };
            }

            // FX default
            return new SlopeProfile
            {
                MinSlopeAtrFactor = 0.06,
                RelaxedSlopeAtrFactor = 0.02
            };
        }

        // -----------------------------------------
        // MAIN CHECK
        // -----------------------------------------
        public static bool IsSlopeValid(
            string symbol,
            double[] emaFast,
            double[] emaSlow,
            double atr,
            bool strongStructure,
            bool strongBreakout)
        {
            if (emaFast == null || emaSlow == null || emaFast.Length < 5 || emaSlow.Length < 5 || atr <= 0)
                return false;

            var profile = GetProfile(symbol);

            // slope = difference over last few candles
            double slopeFast = emaFast[^1] - emaFast[^4];
            double slopeSlow = emaSlow[^1] - emaSlow[^4];

            double absSlopeFast = Math.Abs(slopeFast);
            double absSlopeSlow = Math.Abs(slopeSlow);

            double requiredSlope = atr * profile.MinSlopeAtrFactor;

            // -----------------------------------------
            // NORMAL STRICT CHECK
            // -----------------------------------------
            bool slopeStrong =
                absSlopeFast >= requiredSlope &&
                absSlopeSlow >= requiredSlope * 0.5;

            if (slopeStrong)
                return true;

            // -----------------------------------------
            // RELAXED MODE (STRUCTURE-BASED OVERRIDE)
            // -----------------------------------------
            double relaxedSlope = atr * profile.RelaxedSlopeAtrFactor;

            bool allowRelaxed =
                strongStructure &&
                strongBreakout &&
                absSlopeFast >= relaxedSlope;

            if (allowRelaxed)
            {
                LogFile.Write($"[{symbol}] Slope relaxed due to structure");
                return true;
            }

            LogFile.Write($"[{symbol}] Slope too weak");
            return false;
        }

        // -----------------------------------------
        // HELPERS
        // -----------------------------------------
        private static bool IsGold(string s) =>
            s.Contains("XAU", StringComparison.OrdinalIgnoreCase);

        private static bool IsIndex(string s) =>
            s.Contains("US100", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("NAS", StringComparison.OrdinalIgnoreCase);

        private static bool IsCrypto(string s) =>
            s.Contains("BTC", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("ETH", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("SOL", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("BNB", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("XRP", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("ADA", StringComparison.OrdinalIgnoreCase);
    }

}
