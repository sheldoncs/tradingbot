using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class CompressionDetector
    {
        public static bool IsCompressed(string symbol, MqlRates[] rates, double atr)
        {
            if (rates == null || rates.Length < 6 || atr <= 0)
                return false;

            var c1 = rates[^2];
            var c2 = rates[^3];
            var c3 = rates[^4];
            var c4 = rates[^5];

            double r1 = c1.high - c1.low;
            double r2 = c2.high - c2.low;
            double r3 = c3.high - c3.low;
            double r4 = c4.high - c4.low;

            // --------------------------------
            // MARKET TYPE DETECTION
            // --------------------------------
            bool isIndex =
                symbol.Contains("US100") ||
                symbol.Contains("NAS100") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US30");

            bool isCrypto =
                symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("XRP");

            bool isGold =
                symbol.Contains("XAU");

            bool isFx =
                symbol.EndsWith("USD") && !isGold && !isCrypto;

            // --------------------------------
            // BASE THRESHOLDS
            // --------------------------------
            double compressionFactor = 0.55;
            double contractionFactor = 0.85;   // how much ranges should shrink
            double overlapFactor = 0.6;        // how much candles overlap

            if (isIndex)
            {
                compressionFactor = 0.75;
                contractionFactor = 0.90;
                overlapFactor = 0.5;
            }
            else if (isCrypto)
            {
                compressionFactor = 0.65;
                contractionFactor = 0.88;
                overlapFactor = 0.55;
            }
            else if (isFx)
            {
                compressionFactor = 0.55;
                contractionFactor = 0.85;
                overlapFactor = 0.65;
            }
            else if (isGold)
            {
                compressionFactor = 0.50;
                contractionFactor = 0.82;
                overlapFactor = 0.70;
            }

            // --------------------------------
            // 1. SMALL RANGE CHECK
            // --------------------------------
            int smallCount = 0;

            if (r1 < atr * compressionFactor) smallCount++;
            if (r2 < atr * compressionFactor) smallCount++;
            if (r3 < atr * compressionFactor) smallCount++;
            if (r4 < atr * compressionFactor) smallCount++;

            bool smallRanges = smallCount >= 3;

            // --------------------------------
            // 2. RANGE CONTRACTION (IMPORTANT)
            // --------------------------------
            bool contracting =
                r1 <= r2 * contractionFactor &&
                r2 <= r3 * contractionFactor;

            // --------------------------------
            // 3. CANDLE OVERLAP (COILING)
            // --------------------------------
            double overlap12 = Math.Min(c1.high, c2.high) - Math.Max(c1.low, c2.low);
            double overlap23 = Math.Min(c2.high, c3.high) - Math.Max(c2.low, c3.low);

            bool overlapping =
                overlap12 > (r1 * overlapFactor) &&
                overlap23 > (r2 * overlapFactor);

            // --------------------------------
            // FINAL DECISION
            // --------------------------------
            bool isCompressed =
                smallRanges &&
                (contracting || overlapping);

            return isCompressed;
        }
    }
}
