using System.Collections.Generic;

namespace MetaTrader.Util
{
    public static class SymbolRotationEngine
    {
        // ---- CONFIG ----
        private static readonly HashSet<string> CryptoSymbols = new()
        {
            "SOLUSD",
            "XRPUSD",
            "BNBUSD",
            "XRPUSD",
            "ETHUSD"
        };

        private static readonly HashSet<string> MetalSymbols = new()
        {
            "XAUUSD.s",
            "XAUEUR.s",

        };

        private static readonly HashSet<string> CurrencySymbols = new()
        {
            "US100.cash",
        };

        // Toggle modes
        public enum TradingMode
    {
            CryptoOnly,
            MetalsOnly,
            CurrencyOnly,
            CryptoAndCurrencyOnly,
            Mixed
    }

        // 👇 CHANGE THIS WHEN NEEDED
        public static TradingMode CurrentMode = TradingMode.CryptoOnly;

        public static bool IsSymbolAllowed(string symbol)
        {
            
            switch (CurrentMode)
            {
                case TradingMode.CryptoOnly:
                    return CryptoSymbols.Contains(symbol);
                case TradingMode.CryptoAndCurrencyOnly:
                    return CryptoSymbols.Contains(symbol) || CurrencySymbols.Contains(symbol);
                case TradingMode.MetalsOnly:
                    return MetalSymbols.Contains(symbol);
                case TradingMode.Mixed:
                    return CryptoSymbols.Contains(symbol) || MetalSymbols.Contains(symbol) || CurrencySymbols.Contains(symbol);

                default:
                    return false;
            }
        }
    }

}
