using MetaTrader.Enums;
using MetaTrader.Services.SMC.MetaTrader.Services;
using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class HybridContextEngine
    {
        // =========================================================
        // 📊 ATR AVERAGE — Volatility baseline
        // =========================================================
        public static double CalculateAtrAverage(
            MqlRates[] rates,
            int atrPeriod = 14,
            int lookback = 50
        )
        {
            if (rates.Length < atrPeriod + lookback)
                return 0;

            double sum = 0;
            int count = 0;

            for (int i = atrPeriod; i < atrPeriod + lookback; i++)
            {
                sum += CalculateATR(rates[..^i], atrPeriod);
                count++;
            }

            return count > 0 ? sum / count : 0;
        }

        // =========================================================
        // 💧 LIQUIDITY SWEEP — Stop hunt detection
        // =========================================================
        public static bool DetectLiquiditySweep(MqlRates[] rates)
        {
            if (rates.Length < 5) return false;

            var last = rates[^1];
            var prev2 = rates[^3];

            bool sweepHigh =
                last.high > prev2.high &&
                last.close < prev2.high;

            bool sweepLow =
                last.low < prev2.low &&
                last.close > prev2.low;

            if (sweepHigh || sweepLow)
            {
                Console.WriteLine("💧 Liquidity sweep detected");
                LogFile.Write("Liquidity sweep detected");
            }

            return sweepHigh || sweepLow;
        }

        // =========================================================
        // 🧱 ORDER BLOCK PROXIMITY
        // =========================================================
        public static bool IsNearOrderBlock(
            MetaTrader.Services.SMC.MetaTrader.Services.OrderBlock ob,
            double price,
            double atrRaw,
            double factor = 0.5
        )
        {
            if (ob == null) return false;

            double buffer = atrRaw * factor;

            bool near =
                price >= (ob.Low - buffer) &&
                price <= (ob.High + buffer);

            if (near)
            {
                Console.WriteLine($"📦 Price near Order Block [{ob.Low:F2} - {ob.High:F2}]");
                LogFile.Write($"Price near Order Block [{ob.Low:F2} - {ob.High:F2}]");
            }

            return near;
        }

        // =========================================================
        // 🎯 ENTRY MODE SELECTION (AUTOMATED)
        // =========================================================
        public static EntryMode SelectEntryMode(
            MqlRates[] rates,
            double midPrice,
            double atrRaw,
            TrendContext trend,
            bool h4Weak
        )
        {
            double atrAverage = CalculateAtrAverage(rates);
            bool liquiditySweep = DetectLiquiditySweep(rates);
            MetaTrader.Services.SMC.MetaTrader.Services.OrderBlock bullOB =  SMCEngine.DetectBullishOB(rates, atrRaw);
            MetaTrader.Services.SMC.MetaTrader.Services.OrderBlock bearOB = SMCEngine.DetectBearishOB(rates, atrRaw);

            bool nearOrderBlock =
                IsNearOrderBlock(bullOB, midPrice, atrRaw) ||
                IsNearOrderBlock(bearOB, midPrice, atrRaw);

            // -----------------------------------------------------
            // 🧠 DECISION TREE
            // -----------------------------------------------------
            if (liquiditySweep || nearOrderBlock)
            {
                Console.WriteLine("🔴 EntryMode = SmartMoney (OB / Liquidity)");
                LogFile.Write("EntryMode = SmartMoney");
                return EntryMode.SmartMoney;
            }

            if (h4Weak || atrRaw > atrAverage * 1.4)
            {
                Console.WriteLine("🟠 EntryMode = Hybrid (Volatility / HTF weak)");
                LogFile.Write("EntryMode = Hybrid");
                return EntryMode.Hybrid;
            }

            Console.WriteLine("🔵 EntryMode = Standard (Momentum)");
            LogFile.Write("EntryMode = Standard");
            return EntryMode.Standard;
        }

        private static double CalculateATR(MqlRates[] rates, int period)
        {
            int len = rates.Length;
            if (len <= period)
                return 0;

            double sumTR = 0;

            // start from the point where we have a previous candle
            for (int i = len - period; i < len; i++)
            {
                double high = rates[i].high;
                double low = rates[i].low;
                double prevClose = rates[i - 1].close;

                double tr = Math.Max(high - low,
                             Math.Max(Math.Abs(high - prevClose),
                                      Math.Abs(low - prevClose)));

                sumTR += tr;
            }

            return sumTR / period; // average true range
        }
    }

}
