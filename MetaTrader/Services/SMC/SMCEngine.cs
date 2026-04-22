namespace MetaTrader.Services.SMC
{
    using global::MetaTrader.Util;
    using MtApi5;
    using System;
    using System.Collections.Generic;

    namespace MetaTrader.Services
    {
        // ============================================================
        // 🔶 UNIFIED ORDER BLOCK MODEL
        // ============================================================
        public class OrderBlock
        {
            public double High { get; set; }
            public double Low { get; set; }
            public int Index { get; set; }
            public bool IsBullish { get; set; }
            public bool IsBearish => !IsBullish;
        }

        // ============================================================
        // 🔶 SMC TREND STRUCTURE MODEL (HH, HL, LH, LL)
        // ============================================================
        public class MarketStructure
        {
            public bool HigherHigh { get; set; }
            public bool HigherLow { get; set; }
            public bool LowerHigh { get; set; }
            public bool LowerLow { get; set; }

            public bool BullTrend => HigherHigh && HigherLow;
            public bool BearTrend => LowerHigh && LowerLow;
        }

        // ============================================================
        // 🔶 FAIR VALUE GAP MODEL
        // ============================================================
        public class FairValueGap
        {
            public double Upper { get; set; }
            public double Lower { get; set; }
            public int Index { get; set; }
        }

        // ============================================================
        // 🔥 FULL SMART MONEY CONCEPTS ENGINE
        // ============================================================
        public static class SMCEngine
        {
            // ------------------------------------------------------------
            // 🔍 Detect Bullish Order Block (Down candle before big up-move)
            // ------------------------------------------------------------
            public static OrderBlock DetectBullishOB(MqlRates[] candles, double atr)
            {
                int i = candles.Length - 3;
                if (i < 1) return null;

                var c1 = candles[i];     // down candle
                var c2 = candles[i + 1]; // displacement candle

                bool isDown = c1.close < c1.open;
                bool bigUpMove = (c2.close - c2.open) > atr * 0.7;

                if (isDown && bigUpMove)
                {
                    Console.WriteLine($"[SMC] Bullish OB detected at index {i}");
                    LogFile.Write($"[SMC] Bullish OB detected at index {i}");

                    return new OrderBlock
                    {
                        High = c1.high,
                        Low = c1.low,
                        Index = i,
                        IsBullish = true
                    };
                }

                return null;
            }

            // ------------------------------------------------------------
            // 🔍 Detect Bearish Order Block (Up candle before big down-move)
            // ------------------------------------------------------------
            public static OrderBlock DetectBearishOB(MqlRates[] candles, double atr)
            {
                int i = candles.Length - 3;
                if (i < 1) return null;

                var c1 = candles[i];
                var c2 = candles[i + 1];

                bool isUp = c1.close > c1.open;
                bool bigSell = (c1.open - c1.close) > atr * 0.7;

                if (isUp && bigSell)
                {
                    Console.WriteLine($"[SMC] Bearish OB detected at index {i}");
                    LogFile.Write($"[SMC] Bearish OB detected at index {i}");

                    return new OrderBlock
                    {
                        High = c1.high,
                        Low = c1.low,
                        Index = i,
                        IsBullish = false
                    };
                }

                return null;
            }

            // ------------------------------------------------------------
            // 🔍 Detect Bullish Fair Value Gap
            // ------------------------------------------------------------
            public static FairValueGap DetectBullishFVG(MqlRates[] c)
            {
                int i = c.Length - 3;
                if (i < 1) return null;

                var c0 = c[i];
                var c1 = c[i + 1];
                var c2 = c[i + 2];

                if (c0.high < c2.low)
                {
                    Console.WriteLine($"[SMC] Bullish FVG detected at {i}");
                    LogFile.Write($"[SMC] Bullish FVG detected at {i}");

                    return new FairValueGap
                    {
                        Upper = c2.low,
                        Lower = c0.high,
                        Index = i
                    };
                }

                return null;
            }

            // ------------------------------------------------------------
            // 🔍 Detect Bearish Fair Value Gap
            // ------------------------------------------------------------
            public static FairValueGap DetectBearishFVG(MqlRates[] c)
            {
                int i = c.Length - 3;
                if (i < 1) return null;

                var c0 = c[i];
                var c1 = c[i + 1];
                var c2 = c[i + 2];

                if (c0.low > c2.high)
                {
                    Console.WriteLine($"[SMC] Bearish FVG detected at {i}");
                    LogFile.Write($"[SMC] Bearish FVG detected at {i}");

                    return new FairValueGap
                    {
                        Upper = c0.low,
                        Lower = c2.high,
                        Index = i
                    };
                }

                return null;
            }

            // ------------------------------------------------------------
            // 🔍 Market Structure (HH/HL & LH/LL)
            // ------------------------------------------------------------
            public static MarketStructure DetectStructure(MqlRates[] c)
            {
                int i = c.Length - 1;
                if (i < 3) return new MarketStructure();

                var c1 = c[i];
                var c2 = c[i - 1];
                var c3 = c[i - 2];

                var structure = new MarketStructure
                {
                    HigherHigh = c1.high > c2.high && c2.high > c3.high,
                    HigherLow = c1.low > c2.low && c2.low > c3.low,
                    LowerHigh = c1.high < c2.high && c2.high < c3.high,
                    LowerLow = c1.low < c2.low && c2.low < c3.low
                };

                Console.WriteLine($"[SMC] Structure: HH={structure.HigherHigh}, HL={structure.HigherLow}, LH={structure.LowerHigh}, LL={structure.LowerLow}");
                LogFile.Write($"[SMC] Structure flags: {structure.HigherHigh},{structure.HigherLow},{structure.LowerHigh},{structure.LowerLow}");

                return structure;
            }

            // ------------------------------------------------------------
            // 🔍 Has price mitigated the Order Block?
            // ------------------------------------------------------------
            public static bool IsMitigated(OrderBlock ob, double price)
            {
                if (ob == null) return false;

                bool mitigated =
                    price <= ob.High && price >= ob.Low;

                if (mitigated)
                {
                    Console.WriteLine($"[SMC] OB mitigated at price={price}");
                    LogFile.Write($"[SMC] OB mitigated at price={price}");
                }

                return mitigated;
            }
        }
    }

}
