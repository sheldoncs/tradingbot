using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services.Helper
{
    public static class StallDetectionEngine
    {
        private static Dictionary<string, int> StallCount = new();

        public static bool IsStalling(
            string symbol,
            MqlRates c0,
            double atr,
            out int stallCount)
        {
            stallCount = 0;

            if (atr <= 0)
                return false;

            if (!StallCount.ContainsKey(symbol))
                StallCount[symbol] = 0;

            double bodySize = Math.Abs(c0.close - c0.open);
            double bodyAtr = bodySize / atr;

            double stallThreshold =
                    symbol.Contains("XAU") ? 0.35 :
                    symbol.Contains("US100") ? 0.30 :
                    symbol.Length == 6 ? 0.20 :   // FX
                    0.30;

            bool isStalling = bodyAtr < stallThreshold;

            if (isStalling)
                StallCount[symbol]++;
            else
                StallCount[symbol] = 0;

            stallCount = StallCount[symbol];

            int stallLimit =
                symbol.Contains("XAU") ? 3 :
                symbol.Contains("US100") ? 3 :
                symbol.Length == 6 ? 5 :
                3;

            return stallCount >= stallLimit;
        }
    }
}
