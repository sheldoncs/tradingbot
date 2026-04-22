using MetaTrader.Enums.MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class PriceAcceptanceEngine
    {
        public static bool IsPriceAccepted(
            MqlRates[] rates,
            MqlRates last,
            double[] emaFast,
            double atr,
            TradeDirection direction,
            int structureLookback = 3,
            int shift = 1
        )
        {
            // -----------------------------
            // SAFETY
            // -----------------------------
            if (rates == null || rates.Length < structureLookback + shift + 2)
                return false;

            // -----------------------------
            // CANDLE ANATOMY
            // -----------------------------
            double body = Math.Abs(last.close - last.open);
            double range = last.high - last.low;

            if (range <= 0)
                return false;

            double upperWick = last.high - Math.Max(last.open, last.close);
            double lowerWick = Math.Min(last.open, last.close) - last.low;

            bool strongCandle =
                body / range >= 0.65 &&
                (
                    direction == TradeDirection.Bullish
                        ? upperWick / range <= 0.25
                        : lowerWick / range <= 0.25
                );

            // -----------------------------
            // DIRECTIONAL CANDLE
            // -----------------------------
            bool candleDirection =
                direction == TradeDirection.Bullish
                    ? last.close > last.open
                    : last.close < last.open;

            // -----------------------------
            // STRUCTURE BREAK
            // -----------------------------
            bool brokeStructure =
                direction == TradeDirection.Bullish
                    ? last.close > HighestHigh(rates, structureLookback, shift)
                    : last.close < LowestLow(rates, structureLookback, shift);

            // -----------------------------
            // EMA CONTEXT
            // -----------------------------
            bool emaContext =
                direction == TradeDirection.Bullish
                    ? last.close > emaFast[^1] && emaFast[^1] > emaFast[^2]
                    : last.close < emaFast[^1] && emaFast[^1] < emaFast[^2];

            // -----------------------------
            // ATR OVERRIDE (OPTIONAL)
            // -----------------------------
            bool atrOverride = body >= atr * 0.6;

            // -----------------------------
            // FINAL ACCEPTANCE
            // -----------------------------
            return
                candleDirection &&
                strongCandle &&
                brokeStructure &&
                emaContext
                || atrOverride;
        }

        // -----------------------------
        // HELPERS
        // -----------------------------
        private static double HighestHigh(MqlRates[] rates, int lookback, int shift)
        {
            int end = rates.Length - 1 - shift;
            int start = end - (lookback - 1);

            double highest = double.MinValue;
            for (int i = start; i <= end; i++)
                if (rates[i].high > highest)
                    highest = rates[i].high;

            return highest;
        }

        private static double LowestLow(MqlRates[] rates, int lookback, int shift)
        {
            int end = rates.Length - 1 - shift;
            int start = end - (lookback - 1);

            double lowest = double.MaxValue;
            for (int i = start; i <= end; i++)
                if (rates[i].low < lowest)
                    lowest = rates[i].low;

            return lowest;
        }
    }

}
