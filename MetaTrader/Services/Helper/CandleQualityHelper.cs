using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class CandleQualityHelper
    {
        // --------------------------------
        // PROFILE
        // --------------------------------
        private class CandleProfile
        {
            public double BodyThreshold;     // max % of range for indecision
            public double WickMultiplier;    // how dominant wicks must be
            public double StrongBodyFactor;  // for strong candle detection
        }

        private static CandleProfile GetProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
            {
                return new CandleProfile
                {
                    BodyThreshold = 0.35,     // gold wicks more → allow
                    WickMultiplier = 1.5,
                    StrongBodyFactor = 0.55
                };
            }

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
            {
                return new CandleProfile
                {
                    BodyThreshold = 0.40,
                    WickMultiplier = 1.3,
                    StrongBodyFactor = 0.60
                };
            }

            if (symbol.Contains("BTC") || symbol.Contains("ETH") ||
                symbol.Contains("SOL") || symbol.Contains("AVAX") ||
                symbol.Contains("BNB") || symbol.Contains("XRP"))
            {
                return new CandleProfile
                {
                    BodyThreshold = 0.40,
                    WickMultiplier = 1.2,
                    StrongBodyFactor = 0.60
                };
            }

            // FX default
            return new CandleProfile
            {
                BodyThreshold = 0.45,
                WickMultiplier = 1.2,
                StrongBodyFactor = 0.65
            };
        }

        // --------------------------------
        // 📉 INDECISION / EXHAUSTION
        // --------------------------------
        public static bool IsIndecisionCandle(string symbol, MqlRates candle)
        {
            var profile = GetProfile(symbol);

            double upperWick = candle.high - Math.Max(candle.open, candle.close);
            double lowerWick = Math.Min(candle.open, candle.close) - candle.low;

            double body = Math.Abs(candle.close - candle.open);
            double range = candle.high - candle.low;

            if (range <= 0)
                return false;

            return
                body < range * profile.BodyThreshold &&
                upperWick > body * profile.WickMultiplier &&
                lowerWick > body * profile.WickMultiplier;
        }

        // --------------------------------
        // 🔥 STRONG TREND CANDLE
        // --------------------------------
        public static bool IsStrongTrendCandle(string symbol, MqlRates candle)
        {
            var profile = GetProfile(symbol);

            double body = Math.Abs(candle.close - candle.open);
            double range = candle.high - candle.low;

            if (range <= 0)
                return false;

            double bodyRatio = body / range;

            return bodyRatio >= profile.StrongBodyFactor;
        }

        // --------------------------------
        // 📊 CLOSE POSITION (conviction)
        // --------------------------------
        public static double GetClosePosition(MqlRates candle)
        {
            double range = candle.high - candle.low;
            if (range <= 0)
                return 0.5;

            return (candle.close - candle.low) / range;
        }

        public static bool ClosesNearHigh(MqlRates candle, double threshold = 0.7)
        {
            return GetClosePosition(candle) >= threshold;
        }

        public static bool ClosesNearLow(MqlRates candle, double threshold = 0.7)
        {
            return GetClosePosition(candle) <= (1.0 - threshold);
        }
    }
}
