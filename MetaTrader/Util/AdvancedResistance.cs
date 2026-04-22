using MetaTrader.Data.Entities;
using MtApi5;
using System;
using System.Linq;
using System.Reflection.Emit;

namespace MetaTrader.Util
{
    public static class AdvancedResistance
    {
        // ---------------------------
        // 1. SWING HIGH DETECTION
        // ---------------------------
        public static double FindSwingHigh(MqlRates[] candles, int lookback = 20)
        {
            double highest = double.MinValue;

            for (int i = candles.Length - lookback; i < candles.Length - 1; i++)
            {
                if (i <= 0) continue;

                var c = candles[i];

                bool isSwingHigh =
                    c.high > candles[i - 1].high &&
                    c.high > candles[i + 1].high;

                if (isSwingHigh && c.high > highest)
                    highest = c.high;
            }

            return highest;
        }

        // ---------------------------
        // 2. EQUAL HIGH LIQUIDITY ZONE
        // ---------------------------
        public static bool IsEqualHighZone(MqlRates[] candles, double tolerance = 0.05)
        {
            double lastHigh = candles[^1].high;
            double prevHigh = candles[^2].high;

            return Math.Abs(lastHigh - prevHigh) <= tolerance;
        }

        // ---------------------------
        // 3. SUPPLY ZONE DETECTION
        // ---------------------------
        public static (double top, double bottom) FindSupplyZone(MqlRates[] candles)
        {
            for (int i = candles.Length - 2; i > candles.Length - 15; i--)
            {
                var c = candles[i];

                bool strongBearish =
                    c.open > c.close &&
                    (c.open - c.close) > (c.high - c.low) * 0.5;

                if (strongBearish)
                {
                    double zoneTop = c.high;
                    double zoneBottom = c.open;
                    return (zoneTop, zoneBottom);
                }
            }

            return (0, 0);
        }

        // ---------------------------
        // 4. MASTER RESISTANCE CHECK
        // ---------------------------
        public static FilterResult CheckResistance(
            MqlRates[] candles,
            double midPrice,
            double buffer,
            string SYMBOL,
            double momentumScore,
            double dynamicThreshold)
        {
            // Example: recent high
            double recentHigh = candles.Max(c => c.high);

            bool isNearResistance =
                midPrice >= recentHigh - buffer;

            if (isNearResistance && momentumScore < dynamicThreshold)
            {
                return FilterResult.Block("Near advanced resistance (weak momentum)");
            }

            // A) Swing-high resistance
            double swingHigh = FindSwingHigh(candles);
            if (swingHigh > 0 && midPrice < swingHigh && (swingHigh - midPrice) <= buffer * 1.5)
            {
                Console.WriteLine($"❌ BUY blocked: Swing-high resistance at {swingHigh} {SYMBOL}");
                return FilterResult.Block($"❌ BUY blocked: Swing-high resistance at {swingHigh} {SYMBOL}");
            }

            // B) Equal-high liquidity zone
            if (IsEqualHighZone(candles))
            {
                Console.WriteLine($"❌ BUY blocked: Equal-high liquidity liquidity zone {SYMBOL}");
                return FilterResult.Block($"❌ BUY blocked: Equal-high liquidity liquidity zone {SYMBOL}");
            }

            // C) Supply zone
            var zone = FindSupplyZone(candles);
            if (zone.top > 0 && midPrice <= zone.top && midPrice >= zone.bottom - buffer)
            {
                Console.WriteLine($"❌ BUY blocked: Supply zone resistance {SYMBOL}");
                return FilterResult.Block($"❌ BUY blocked: Supply zone resistance {SYMBOL}");
            }

            

            return FilterResult.Allow();
        }
    }

}
