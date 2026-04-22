using MetaTrader.Util;
using System;

namespace MetaTrader.Services
{
    public static class EmaCompressionFilter
    {
        public static bool IsCompressed(
            double[] emaFast,
            double[] emaSlow,
            double atr,
            string symbol)
        {
            if (emaFast == null || emaSlow == null || emaFast.Length < 2 || emaSlow.Length < 2 || atr <= 0)
                return true;

            symbol = (symbol ?? string.Empty).ToUpperInvariant();

            double compressionFactor = GetCompressionFactor(symbol);

            double emaDistance = Math.Abs(emaFast[^1] - emaSlow[^1]);
            double threshold = atr * compressionFactor;

            bool compressed = emaDistance < threshold;

            if (compressed)
            {
                string profile =
                    IsGold(symbol) ? "GOLD" :
                    IsIndex(symbol) ? "INDEX" :
                    IsCrypto(symbol) ? "CRYPTO" : "FX";

                string message =
                    $"[{symbol}] EMA compression detected ({profile}) | " +
                    $"dist={emaDistance:F5} < thr={threshold:F5} | factor={compressionFactor:F2}";

                Console.WriteLine(message);
                LogFile.Write(message);
            }

            return compressed;
        }

        private static double GetCompressionFactor(string symbol)
        {
            if (IsGold(symbol))
                return 0.12;   // stricter than before; avoids over-blocking after ATR expands

            if (IsIndex(symbol))
                return 0.14;   // balanced

            if (IsCrypto(symbol))
                return 0.18;   // looser, crypto needs more room

            return 0.10;       // FX = strictest
        }

        private static bool IsGold(string symbol)
            => !string.IsNullOrWhiteSpace(symbol) &&
               symbol.Contains("XAU");

        private static bool IsCrypto(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;

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

            return symbol.Contains("US100") ||
                   symbol.Contains("NAS100") ||
                   symbol.Contains("USTEC") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("SPX");
        }
    }
}
