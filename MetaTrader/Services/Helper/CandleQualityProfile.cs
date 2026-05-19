namespace MetaTrader.Services.Helper
{
    public static class CandleQualityProfile
    {
        public static double GetMinimumBodyRatio(
            string symbol)
        {
            symbol = symbol.ToUpper();

            // -------------------------
            // CRYPTO
            // -------------------------
            if (symbol.Contains("ETH") ||
                symbol.Contains("BTC") ||
                symbol.Contains("BNB") ||
                symbol.Contains("SOL") ||
                symbol.Contains("XRP"))
            {
                return 0.45;
            }

            // -------------------------
            // GOLD
            // -------------------------
            if (symbol.Contains("XAU"))
            {
                return 0.50;
            }

            // -------------------------
            // INDICES
            // -------------------------
            if (symbol.Contains("US100") ||
                symbol.Contains("NAS"))
            {
                return 0.52;
            }

            // -------------------------
            // FX
            // -------------------------
            return 0.60;
        }
    }
}
