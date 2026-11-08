using System.Collections.Generic;

namespace MetaTrader.Util
{
    public static class SymbolPriority
    {
        private static readonly Dictionary<string, double> CryptoPriority = new()
        {
             { "BNBUSD", 0.30 },
             { "SOLUSD", 0.18 },
             { "ETHUSD", 0.15 },
        };

        private static readonly Dictionary<string, double> GoldPriority = new()
        {
            { "XAUUSD.s", 0.60 },
            { "XAUEUR.s", 0.40 },
        };

        private static readonly Dictionary<string, double> IndexPriority = new()
        {
            { "US100.cash", 0.10 }
        };

        public static double Get(string symbol, bool indexWindow)
        {
            if (indexWindow)
                return GoldPriority.TryGetValue(symbol, out var q) ? q : 0;

            return GoldPriority.TryGetValue(symbol, out var p) ? p : 0;
        }

    }

}
