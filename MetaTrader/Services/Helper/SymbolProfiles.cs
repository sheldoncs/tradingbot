using MetaTrader.Data.Entities;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services.Helper
{
    public static class SymbolProfiles
    {
        private static readonly Dictionary<string, SymbolProfile> _profiles =
            new Dictionary<string, SymbolProfile>(StringComparer.OrdinalIgnoreCase)
            {
                // 🟡 GOLD (fast, spiky, dangerous late entries)
                ["XAUUSD.s"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.25,
                    ExtensionFactor = 0.6
                },
                ["XAUEUR.s"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.25,
                    ExtensionFactor = 0.6
                },

                // 🟢 FX (slower, smoother)
                ["GBPUSD"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.35,
                    ExtensionFactor = 0.8
                },
                ["EURUSD"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.35,
                    ExtensionFactor = 0.8
                },
                ["USDJPY"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.35,
                    ExtensionFactor = 0.8
                },

                // 🔵 CRYPTO (medium volatility, but can spike)
                ["ETHUSD"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.30,
                    ExtensionFactor = 0.7
                },
                ["SOLUSD"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.30,
                    ExtensionFactor = 0.7
                },
                ["BNBUSD"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.30,
                    ExtensionFactor = 0.7
                },

                // 🟣 INDEX
                ["US100"] = new SymbolProfile
                {
                    WeakStructureThreshold = 0.28,
                    ExtensionFactor = 0.65
                }
            };

        public static SymbolProfile Get(string symbol)
        {
            if (_profiles.TryGetValue(symbol, out var profile))
                return profile;

            // 🔁 fallback (VERY important)
            return new SymbolProfile
            {
                WeakStructureThreshold = 0.30,
                ExtensionFactor = 0.7
            };
        }
    }
}
