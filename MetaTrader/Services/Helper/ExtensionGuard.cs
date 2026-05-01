using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Helper
{
    public static class ExtensionGuard
    {
        private class ExtensionProfile
        {
            public double MaxAtrMultiple;
        }

        // --------------------------------
        // PROFILE PER SYMBOL TYPE
        // --------------------------------
        private static ExtensionProfile GetProfile(string symbol)
        {
            if (IsGold(symbol))
                return new ExtensionProfile
                {
                    MaxAtrMultiple = 1.2   // gold moves fast but not like crypto
                };

            if (IsIndex(symbol))
                return new ExtensionProfile
                {
                    MaxAtrMultiple = 1.3   // indices trend smoother
                };

            if (IsCrypto(symbol))
                return new ExtensionProfile
                {
                    MaxAtrMultiple = 1.6   // crypto is very volatile
                };

            // FX default (tightest)
            return new ExtensionProfile
            {
                MaxAtrMultiple = 1.0
            };
        }

        // --------------------------------
        // MAIN CHECK
        // --------------------------------
        public static bool IsTooExtended(
            string symbol,
            double currentPrice,
            double ema,
            double atr)
        {
            if (atr <= 0)
                return false;

            var profile = GetProfile(symbol);

            double distanceFromEMA = Math.Abs(currentPrice - ema);

            if (distanceFromEMA > atr * profile.MaxAtrMultiple)
            {
                LogFile.Write($"❌ {symbol} blocked: too extended from EMA (dist={distanceFromEMA:F2}, atr={atr:F2})");
                return true;
            }

            return false;
        }

        public static bool IsTooExtendedForVelocity(
            string symbol,
            double currentPrice,
            double ema,
            double atr)
        {
            var profile = GetProfile(symbol);

            double distanceFromEMA = Math.Abs(currentPrice - ema);

            // allow more extension for velocity
            double velocityMultiplier = profile.MaxAtrMultiple * 1.5;

            return distanceFromEMA > atr * velocityMultiplier;
        }
        // --------------------------------
        // HELPERS
        // --------------------------------
        private static bool IsGold(string s) =>
            s.Contains("XAU");

        private static bool IsIndex(string s) =>
            s.Contains("US100") || s.Contains("NAS") || s.Contains("SPX");

        private static bool IsCrypto(string s) =>
            s.Contains("BTC") || s.Contains("ETH") || s.Contains("SOL") || s.Contains("BNB") || s.Contains("XRP");
    }
}
