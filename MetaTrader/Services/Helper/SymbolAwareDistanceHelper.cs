using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Helper
{
    public static class SymbolAwareDistanceHelper
    {
        // --------------------------------
        // PROFILE PER SYMBOL TYPE
        // --------------------------------
        private class DistanceProfile
        {
            public double MaxAtrMultiplier;
        }

        private static DistanceProfile GetProfile(string symbol)
        {
            if (IsGold(symbol))
            {
                return new DistanceProfile
                {
                    MaxAtrMultiplier = 1.4 // Gold needs more room
                };
            }

            if (IsCrypto(symbol))
            {
                return new DistanceProfile
                {
                    MaxAtrMultiplier = 1.2 // Crypto moderate
                };
            }

            if (IsIndex(symbol))
            {
                return new DistanceProfile
                {
                    MaxAtrMultiplier = 1.3 // Index swings
                };
            }

            // FX (default)
            return new DistanceProfile
            {
                MaxAtrMultiplier = 1.0 // FX is tighter
            };
        }

        // --------------------------------
        // MAIN CHECK
        // --------------------------------
        public static bool IsTooExtended(
            string symbol,
            double currentPrice,
            double emaFast,
            double atr)
        {
            if (atr <= 0)
                return false;

            var profile = GetProfile(symbol);

            double distanceFromEMA = Math.Abs(currentPrice - emaFast);

            if (distanceFromEMA > atr * profile.MaxAtrMultiplier)
            {
                LogFile.Write(
                    $"❌ {symbol} too extended | dist={distanceFromEMA:F2} > ATR*{profile.MaxAtrMultiplier}");
                return true;
            }

            return false;
        }

        // --------------------------------
        // OPTIONAL: STRICT MODE (for cascade)
        // --------------------------------
        public static bool IsTooExtendedStrict(
            string symbol,
            double currentPrice,
            double emaFast,
            double atr)
        {
            if (atr <= 0)
                return false;

            var profile = GetProfile(symbol);

            double stricter = profile.MaxAtrMultiplier * 0.85;

            double distanceFromEMA = Math.Abs(currentPrice - emaFast);

            if (distanceFromEMA > atr * stricter)
            {
                LogFile.Write(
                    $"❌ {symbol} too extended (strict) | dist={distanceFromEMA:F2} > ATR*{stricter:F2}");
                return true;
            }

            return false;
        }

        // --------------------------------
        // SYMBOL HELPERS
        // --------------------------------
        private static bool IsGold(string s) =>
            s.Contains("XAU");

        private static bool IsCrypto(string s) =>
            s.Contains("BTC") || s.Contains("ETH") || s.Contains("SOL") || s.Contains("XRP") || s.Contains("BNB");

        private static bool IsIndex(string s) =>
            s.Contains("US100") || s.Contains("NAS") || s.Contains("SPX");
    }
}
