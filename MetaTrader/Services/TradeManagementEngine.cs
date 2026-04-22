using MetaTrader.Enums;
using MtApi5;
using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Services
{
    public static class TradeManagementEngine
    {
        private static bool IsGold(string symbol) => symbol.Contains("XAU");
        private static bool IsCrypto(string symbol) =>
            symbol.Contains("BTC") || symbol.Contains("ETH") ||
            symbol.Contains("SOL") || symbol.Contains("BNB");

        private static bool IsIndex(string symbol) =>
            symbol.Contains("US100") || symbol.Contains("NAS");

        private static bool IsFx(string symbol) =>
            !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);

        public static bool ShouldExit(
            string symbol,
            TradeAction direction,
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow,
            double currentMomentum,
            double previousMomentum,
            MomentumResult momentumResult)
        {
            if (rates == null || rates.Length < 3 ||
                emaFast.Length < 3 || emaSlow.Length < 3)
                return false;

            var last = rates[^1];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            bool momentumWeakening = momentumResult.Falling || momentumResult.Decelerating;
            // --------------------------------
            // GOLD (FAST EXIT)
            // --------------------------------
            if (IsGold(symbol))
            {
                if (direction == TradeAction.Bullish &&
                    last.close < ema9 && momentumWeakening)
                    return true;

                if (direction == TradeAction.Bearish &&
                    last.close > ema9 && momentumWeakening)
                    return true;
            }

            // --------------------------------
            // FX (BALANCED)
            // --------------------------------
            if (IsFx(symbol))
            {
                if (direction == TradeAction.Bullish &&
                    last.close < ema9 && currentMomentum < previousMomentum * 0.9)
                    return true;

                if (direction == TradeAction.Bearish &&
                    last.close > ema9 && currentMomentum < previousMomentum * 0.9)
                    return true;
            }

            // --------------------------------
            // INDEX (LET IT RUN)
            // --------------------------------
            if (IsIndex(symbol))
            {
                if (direction == TradeAction.Bullish &&
                    last.close < ema21)
                    return true;

                if (direction == TradeAction.Bearish &&
                    last.close > ema21)
                    return true;
            }

            // --------------------------------
            // CRYPTO (HYBRID)
            // --------------------------------
            if (IsCrypto(symbol))
            {
                if (direction == TradeAction.Bullish &&
                    last.close < ema9 && momentumWeakening)
                    return true;

                if (direction == TradeAction.Bearish &&
                    last.close > ema9 && momentumWeakening)
                    return true;
            }

            return false;
        }
    }
}
