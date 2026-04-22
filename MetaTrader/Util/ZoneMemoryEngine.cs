using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Util
{
    public static class ZoneMemoryEngine
    {
        private static List<double> supportZones = new();
        private static List<double> resistanceZones = new();

        public static void UpdateZones(MqlRates candle)
        {
            // Identify significant swing lows/highs
            if (candle.close < candle.open && (candle.high - candle.low) > 10)
                supportZones.Add(candle.low);

            if (candle.close > candle.open && (candle.high - candle.low) > 10)
                resistanceZones.Add(candle.high);

            // Limit memory size
            if (supportZones.Count > 20) supportZones.RemoveAt(0);
            if (resistanceZones.Count > 20) resistanceZones.RemoveAt(0);
        }

        public static bool IsNearSupport(double price, double atr)
        {
            return supportZones.Any(z => Math.Abs(price - z) < atr * 0.8);
        }

        public static bool IsNearResistance(double price, double atr)
        {
            return resistanceZones.Any(z => Math.Abs(price - z) < atr * 0.8);
        }
    }

}

