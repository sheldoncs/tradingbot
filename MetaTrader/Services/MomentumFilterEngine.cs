namespace MetaTrader.Services
{
    public static class MomentumFilterEngine
    {
        public static bool ShouldBlockTrade(
            string symbol,
            double momentum,
            double atrPercentile,
            out double effectiveThreshold,
            out double baseThreshold)
        {
            // Normalize ATR percentile (handle 0–100 or 0–1 input)
            double atrNormalized = atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            // Get base threshold from adaptive engine
            baseThreshold = AdaptiveMomentumEngine.GetThreshold(symbol, atrNormalized);
            effectiveThreshold = baseThreshold;

            // 🔍 Detect asset class
            bool isCrypto = IsCrypto(symbol);
            bool isGold = IsGold(symbol);
            bool isIndex = IsIndex(symbol);
            bool isFx = !(isCrypto || isGold || isIndex);

            // Default tuning
            double nearFactor = 0.75;
            double relaxFactor = 0.90;
            double highVolThreshold = 0.85;

            // 🔥 Asset-specific tuning
            if (isCrypto)
            {
                nearFactor = 0.75;
                relaxFactor = 0.90;
                highVolThreshold = 0.85;
            }
            else if (isGold)
            {
                nearFactor = 0.78;
                relaxFactor = 0.92;
                highVolThreshold = 0.88;
            }
            else if (isIndex)
            {
                nearFactor = 0.77;
                relaxFactor = 0.91;
                highVolThreshold = 0.85;
            }
            else // FX
            {
                nearFactor = 0.80;
                relaxFactor = 0.95;
                highVolThreshold = 0.90;
            }

            // 🔥 EARLY IMPULSE RELAXATION
            bool isHighVol = atrNormalized > highVolThreshold;
            bool isNearThreshold = momentum >= baseThreshold * nearFactor;

            if (isHighVol && isNearThreshold)
            {
                effectiveThreshold *= relaxFactor;
            }

            // 🔍 FINAL DECISION
            return momentum < effectiveThreshold;
        }

        // =========================
        // 🔧 Helpers (same logic as your engine)
        // =========================

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
    }
}
