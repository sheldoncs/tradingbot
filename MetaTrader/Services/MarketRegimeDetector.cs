using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class MarketRegimeDetector
    {
        public enum Regime
        {
            Dead,
            Chop,
            Building,
            Tradable,
            Expansion
        }

        public enum AssetProfile
        {
            Gold,
            Fx,
            Crypto,
            Index
        }

        // -----------------------------------------
        // PUBLIC ENTRY
        // -----------------------------------------
        public static Regime Detect(
            string symbol,
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow,
            double atr)
        {
            if (rates == null || rates.Length < 10 ||
                emaFast == null || emaFast.Length < 5 ||
                emaSlow == null || emaSlow.Length < 5)
            {
                return Regime.Dead;
            }

            var profile = GetProfile(symbol);

            var c0 = rates[^1];
            var c1 = rates[^2];
            var c2 = rates[^3];
            var c3 = rates[^4];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            // -----------------------------------------
            // PROFILE-BASED TUNING
            // -----------------------------------------
            double expansionFactor;
            double slopeFactor;
            double structureFactor;

            switch (profile)
            {
                case AssetProfile.Gold:
                    expansionFactor = 1.4;
                    slopeFactor = 1.1;
                    structureFactor = 1.0;
                    break;

                case AssetProfile.Crypto:
                    expansionFactor = 1.6;
                    slopeFactor = 1.2;
                    structureFactor = 1.1;
                    break;

                case AssetProfile.Index:
                    expansionFactor = 1.3;
                    slopeFactor = 1.1;
                    structureFactor = 1.0;
                    break;

                default: // FX
                    expansionFactor = 1.1;
                    slopeFactor = 1.0;
                    structureFactor = 0.9;
                    break;
            }

            // -----------------------------------------
            // 1. TREND (EMA ALIGNMENT + SLOPE)
            // -----------------------------------------
            double slopeNow = emaFast[^1] - emaFast[^2];
            double slopePrev = emaFast[^2] - emaFast[^3];

            bool slopeStrong = Math.Abs(slopeNow) > Math.Abs(slopePrev) * slopeFactor;

            bool trendAligned =
                (ema9 > ema21 && slopeNow > 0) ||
                (ema9 < ema21 && slopeNow < 0);

            // -----------------------------------------
            // 2. VOLATILITY / EXPANSION
            // -----------------------------------------
            double range0 = c0.high - c0.low;
            double range1 = c1.high - c1.low;

            bool expansionNow = range0 > atr * expansionFactor;
            bool expansionRecent = range1 > atr * (expansionFactor * 0.8);

            // -----------------------------------------
            // 3. MOMENTUM CONSISTENCY
            // -----------------------------------------
            bool bullishMomentum = c0.close > c0.open && c1.close > c1.open;
            bool bearishMomentum = c0.close < c0.open && c1.close < c1.open;

            bool cleanMomentum = bullishMomentum || bearishMomentum;

            // -----------------------------------------
            // 4. STRUCTURE
            // -----------------------------------------
            bool higherLow = c1.low > c2.low;
            bool lowerHigh = c1.high < c2.high;

            bool structureForming =
                (higherLow || lowerHigh) &&
                Math.Abs(c1.close - c1.open) < atr * structureFactor;

            // -----------------------------------------
            // REGIME DECISION
            // -----------------------------------------

            // 🔥 EXPANSION (money phase)
            if (trendAligned && slopeStrong && expansionNow && cleanMomentum)
                return Regime.Expansion;

            // ✅ TRADABLE
            if (trendAligned && structureForming && slopeStrong)
                return Regime.Tradable;

            // 🟡 BUILDING
            if (structureForming)
                return Regime.Building;

            // ⚠️ CHOP
            if (!trendAligned && !expansionNow)
                return Regime.Chop;

            return Regime.Dead;
        }

        // -----------------------------------------
        // PROFILE DETECTION
        // -----------------------------------------
        private static AssetProfile GetProfile(string symbol)
        {
            symbol = symbol.ToUpper();

            if (symbol.Contains("XAU"))
                return AssetProfile.Gold;

            if (symbol.Contains("ETH") || symbol.Contains("BTC") || symbol.Contains("SOL") || symbol.Contains("XRP"))
                return AssetProfile.Crypto;

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
                return AssetProfile.Index;

            return AssetProfile.Fx;
        }
    }
}
