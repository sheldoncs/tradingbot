using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public static class AdaptiveMomentumEngine
    {
        public class MomentumProfile
        {
            public double BaseThreshold { get; set; }
            public double VolatilityAdjustment { get; set; }
            public double MinThreshold { get; set; }
            public double MaxThreshold { get; set; }
        }

        private static readonly Dictionary<string, MomentumProfile> Profiles = new()
    {
        { "GOLD", new MomentumProfile
            {
                BaseThreshold = 4.2,
                VolatilityAdjustment = 2.0,
                MinThreshold = 2.8,
                MaxThreshold = 6.2
            }
        },
        { "FX", new MomentumProfile
            {
                BaseThreshold = 3.2,
                VolatilityAdjustment = 1.8,
                MinThreshold = 2.0,
                MaxThreshold = 4.5
            }
        },
        { "CRYPTO", new MomentumProfile
            {
                BaseThreshold = 3.6,
                VolatilityAdjustment = 2.0,
                MinThreshold = 2.2,
                MaxThreshold = 5.5
            }
        },
        { "INDEX", new MomentumProfile
            {
                BaseThreshold = 3.8,
                VolatilityAdjustment = 1.8,
                MinThreshold = 2.5,
                MaxThreshold = 5.5
            }
        }
    };

        private static bool IsGold(string symbol)
            => !string.IsNullOrWhiteSpace(symbol) &&
               symbol.ToUpperInvariant().Contains("XAU");

        private static bool IsCrypto(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            symbol = symbol.ToUpperInvariant();

            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("LINK");
        }

        private static bool IsIndex(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            symbol = symbol.ToUpperInvariant();

            return symbol.Contains("US100") ||
                   symbol.Contains("NAS100") ||
                   symbol.Contains("USTEC") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("SPX");
        }

        private static bool IsFx(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            symbol = symbol.ToUpperInvariant();

            if (IsGold(symbol) || IsCrypto(symbol) || IsIndex(symbol))
                return false;

            return symbol.Length >= 6 &&
                   (symbol.EndsWith("USD") ||
                    symbol.EndsWith("JPY") ||
                    symbol.EndsWith("EUR") ||
                    symbol.EndsWith("GBP") ||
                    symbol.EndsWith("AUD") ||
                    symbol.EndsWith("NZD") ||
                    symbol.EndsWith("CAD") ||
                    symbol.EndsWith("CHF"));
        }

        private static MomentumProfile GetProfile(string symbol)
        {
            if (IsGold(symbol)) return Profiles["GOLD"];
            if (IsIndex(symbol)) return Profiles["INDEX"];
            if (IsCrypto(symbol)) return Profiles["CRYPTO"];
            return Profiles["FX"];
        }

        public static double GetThreshold(string symbol, double atrPercentile)
        {
            var profile = GetProfile(symbol);

            // Clamp ATR percentile to [0,1]
            atrPercentile = Math.Max(0.0, Math.Min(1.0, atrPercentile));

            // Linear adjustment around 0.5
            double adjustment = (atrPercentile - 0.5) * profile.VolatilityAdjustment;

            double threshold = profile.BaseThreshold + adjustment;

            // 🔥 LOW VOL RELAXATION (critical fix)
            if (atrPercentile < 0.30)
            {
                threshold -= 0.4; // allow structuring trades
            }

            // Clamp final threshold
            threshold = Math.Max(profile.MinThreshold, Math.Min(profile.MaxThreshold, threshold));

            return threshold;
        }

        public static bool IsMomentumStrong(
            string symbol,
            double momentum,
            double atrPercentile,
            out double threshold)
        {
            threshold = GetThreshold(symbol, atrPercentile);
            return momentum >= threshold;
        }
    }
}
