using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class AdvancedSupport
    {
        // ------------------------------------------------------
        // 1) SWING-LOW DETECTION (Support Cluster)
        // ------------------------------------------------------
        public static double FindSwingLow(MqlRates[] candles, int lookback = 20)
        {
            double lowest = double.MaxValue;

            for (int i = candles.Length - lookback; i < candles.Length - 1; i++)
            {
                if (i <= 0) continue;

                bool isSwingLow =
                    candles[i].low < candles[i - 1].low &&
                    candles[i].low < candles[i + 1].low;

                if (isSwingLow && candles[i].low < lowest)
                    lowest = candles[i].low;
            }

            return lowest == double.MaxValue ? 0 : lowest;
        }

        // ------------------------------------------------------
        // 2) EQUAL-LOW LIQUIDITY ZONE (Potential stop-hunt area)
        // ------------------------------------------------------
        public static bool IsEqualLowZone(MqlRates[] candles, double tolerance = 0.05)
        {
            double lastLow = candles[^1].low;
            double prevLow = candles[^2].low;

            return Math.Abs(lastLow - prevLow) <= tolerance;
        }

        // ------------------------------------------------------
        // 3) DEMAND ZONE DETECTION (Bullish imbalance)
        // ------------------------------------------------------
        public static (double top, double bottom) FindDemandZone(MqlRates[] candles)
        {
            // Scan recent bearish-to-bullish shift candles
            for (int i = candles.Length - 2; i > candles.Length - 15; i--)
            {
                var c = candles[i];

                bool strongBullish =
                    c.close > c.open &&
                    (c.close - c.open) > (c.high - c.low) * 0.5;

                if (strongBullish)
                {
                    double zoneBottom = c.low;
                    double zoneTop = c.close; // or c.open if you prefer conservative
                    return (zoneTop, zoneBottom);
                }
            }

            return (0, 0);
        }

        // ------------------------------------------------------
        // 4) MASTER SUPPORT CHECK (full logic bundle)
        // ------------------------------------------------------
        public static FilterResult CheckSupport(
            MqlRates[] candles,
            double midPrice,
            double buffer,
            string SYMBOL)
        {
            // A) Swing-low support
            double swingLow = FindSwingLow(candles);
            if (swingLow > 0 && midPrice > swingLow && (midPrice - swingLow) <= buffer * 1.5)
            {
                Console.WriteLine($"❌ SELL blocked: Swing-low support at {swingLow} {SYMBOL}");
                return FilterResult.Block(
                    $"❌ SELL blocked: Swing-low support at {swingLow} {SYMBOL}");
            }

            // B) Equal-low liquidity (higher chance of bounce)
            if (IsEqualLowZone(candles))
            {
                Console.WriteLine($"❌ SELL blocked: Equal-low liquidity zone (demand sweep likely) {SYMBOL}");
                return FilterResult.Block(
                    $"❌ SELL blocked: Equal-low liquidity zone (demand sweep likely) {SYMBOL}");
            }

            // C) Demand zone (buy imbalance)
            var zone = FindDemandZone(candles);
            if (zone.top > 0 && midPrice >= zone.bottom && midPrice <= zone.top + buffer)
            {
                Console.WriteLine($"❌ SELL blocked: Demand zone support {SYMBOL} bottom={zone.bottom} top={zone.top}");
                return FilterResult.Block(
                    $"❌ SELL blocked: Demand zone support {SYMBOL}");
            }

            return FilterResult.Allow();
        }
    }

}
