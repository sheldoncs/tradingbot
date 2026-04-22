using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;

namespace MetaTrader.Services
{
    public static class ResistanceSupportGuard
    {
        // --------------------------------
        // ASSET DETECTION
        // --------------------------------
        private static bool IsGold(string symbol)
            => symbol.Contains("XAU");

        private static bool IsCrypto(string symbol)
            => symbol.Contains("BTC") || symbol.Contains("ETH") ||
               symbol.Contains("SOL") || symbol.Contains("BNB") ||
               symbol.Contains("XRP") || symbol.Contains("ADA") ||
               symbol.Contains("AVAX") || symbol.Contains("LINK");

        private static bool IsIndex(string symbol)
            => symbol.Contains("US100") || symbol.Contains("NAS") ||
               symbol.Contains("USTEC") || symbol.Contains("US30");

        private static bool IsFx(string symbol)
            => !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);

        // --------------------------------
        // ADAPTIVE BUFFER
        // --------------------------------
        private static double GetAtrMultiplier(string symbol)
        {
            if (IsGold(symbol)) return 0.6;
            if (IsFx(symbol)) return 0.45;
            if (IsIndex(symbol)) return 0.55;
            if (IsCrypto(symbol)) return 0.7;

            return 0.5;
        }

        // --------------------------------
        // MAIN LOGIC
        // --------------------------------
        public static bool TooCloseToResistanceOrSupport(
            string symbol,
            MqlRates[] rates,
            TradeAction action,
            double atr,
            int lookback = 20)
        {
            if (rates == null || rates.Length < lookback + 2 || atr <= 0)
                return false;

            double highest = double.MinValue;
            double lowest = double.MaxValue;

            for (int i = 1; i <= lookback; i++)
            {
                if (rates[i].high > highest)
                    highest = rates[i].high;

                if (rates[i].low < lowest)
                    lowest = rates[i].low;
            }

            double currentPrice = rates[0].close;
            double atrMultiplier = GetAtrMultiplier(symbol);

            // --------------------------------
            // BUY
            // --------------------------------
            if (action == TradeAction.EnterBuy || action == TradeAction.Buy)
            {
                double distanceToHigh = highest - currentPrice;

                if (distanceToHigh <= atr * atrMultiplier)
                {
                    LogFile.Write($"[{symbol}] ❌ Buy blocked: too close to resistance");
                    return true;
                }
            }

            // --------------------------------
            // SELL
            // --------------------------------
            if (action == TradeAction.EnterSell || action == TradeAction.Sell)
            {
                double distanceToLow = currentPrice - lowest;

                if (distanceToLow <= atr * atrMultiplier)
                {
                    LogFile.Write($"[{symbol}] ❌ Sell blocked: too close to support");
                    return true;
                }
            }

            return false;
        }
    }
}
