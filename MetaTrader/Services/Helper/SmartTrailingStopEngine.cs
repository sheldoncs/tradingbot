using MetaTrader.Enums;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services.Helper
{
    public static class SmartTrailingStopEngine
    {
        // Store highest/lowest price reached per trade
        private static readonly Dictionary<string, double> HighestPrice = new();
        private static readonly Dictionary<string, double> LowestPrice = new();

        public static double CalculateTrailingStop(
            string symbol,
            TradeAction side,
            double currentPrice,
            double atr,
            double ema9,
            double multiplier = 1.5)
        {
            // -------------------------------
            // INIT TRACKING
            // -------------------------------
            if (!HighestPrice.ContainsKey(symbol))
                HighestPrice[symbol] = currentPrice;

            if (!LowestPrice.ContainsKey(symbol))
                LowestPrice[symbol] = currentPrice;

            // -------------------------------
            // BUY LOGIC
            // -------------------------------
            if (side == TradeAction.EnterBuy)
            {
                // Track highest price reached
                if (currentPrice > HighestPrice[symbol])
                    HighestPrice[symbol] = currentPrice;

                double trailFromHigh = HighestPrice[symbol] - (atr * multiplier);

                // Optional: EMA tightening (more aggressive protection)
                double emaBasedStop = ema9 - (atr * 0.5);

                return Math.Max(trailFromHigh, emaBasedStop);
            }

            // -------------------------------
            // SELL LOGIC
            // -------------------------------
            else
            {
                // Track lowest price reached
                if (currentPrice < LowestPrice[symbol])
                    LowestPrice[symbol] = currentPrice;

                double trailFromLow = LowestPrice[symbol] + (atr * multiplier);

                double emaBasedStop = ema9 + (atr * 0.5);

                return Math.Min(trailFromLow, emaBasedStop);
            }
        }

        // Reset after trade closes
        public static void Reset(string symbol)
        {
            if (HighestPrice.ContainsKey(symbol))
                HighestPrice.Remove(symbol);

            if (LowestPrice.ContainsKey(symbol))
                LowestPrice.Remove(symbol);
        }
    }
}
