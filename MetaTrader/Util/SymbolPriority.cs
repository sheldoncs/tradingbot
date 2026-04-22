using System.Collections.Generic;

namespace MetaTrader.Util
{
    public static class SymbolPriority
    {
        private static readonly Dictionary<string, double> CryptoPriority = new()
        {
            { "ETHUSD", 0.35 },
            { "SOLUSD", 0.30 },
            { "BNBUSD", 0.20 },
            { "XRPUSD", 0.15 },
        };

        private static readonly Dictionary<string, double> GoldPriority = new()
        {
            { "XAUUSD", 0.60 },
            { "XAUEUR", 0.40 },
        };

        public static double Get(string symbol, bool cryptoWindow)
        {
            if (cryptoWindow)
                return CryptoPriority.TryGetValue(symbol, out var q) ? q : 0;

            return GoldPriority.TryGetValue(symbol, out var p) ? p : 0;
        }

    }

}
