using Microsoft.AspNetCore.Mvc;
using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class FilterEngine
    {
        public static FilterResult Check(
            MqlRates[] rates1H,
            double atrPoints,
            double midPrice,
            bool h1Bull,
            bool h1Bear,
            MqlRates last,
            bool bullishBreakout,
            bool bearishBreakout,
            double[] emaFast1H,
            double[] emaSlow1H,
            string SYMBOL,
            double atr,                      // atrRaw
            double momentumScore,
            double dynamicThreshold
        )
        {
            if (rates1H.Length < 3)
                return FilterResult.Block("Not enough candles for filter checks.");

            MqlRates prev = rates1H[^2];
            MqlRates prev2 = rates1H[^3];

            double previousHigh = prev.high;
            double previousLow = prev.low;

            double minBuffer = SYMBOL == "XAUUSD" ? 0.50 : 0.20;
            double buffer = Math.Max(atrPoints * 0.05, minBuffer);

            bool isBelowHigh = midPrice < previousHigh;
            bool isNearHigh = isBelowHigh && (previousHigh - midPrice) <= buffer;
            bool isAtHigh = Math.Abs(midPrice - previousHigh) < buffer * 0.1;
            bool isAboveHigh = midPrice > previousHigh;

            bool isAboveLow = midPrice > previousLow;
            bool isNearLow = isAboveLow && (midPrice - previousLow) <= buffer;
            bool isAtLow = Math.Abs(midPrice - previousLow) < buffer * 0.1;
            bool isBelowLow = midPrice < previousLow;

            bool breakoutUpConfirmed = last.close > previousHigh;
            bool breakoutDownConfirmed = last.close < previousLow;

            bool earlyBearStructure = last.high < prev.high && last.low < prev.low;
            bool earlyBullStructure = last.high > prev.high && last.low > prev.low;

            double lastBody = Math.Abs(last.close - last.open);

            // IMPROVED impulse logic (using atrRaw, not atrPoints)
            bool impulseBear =
                lastBody >= atr * 0.7 &&
                (last.close - last.low) <= atr * 0.15;

            bool impulseBull =
                lastBody >= atr * 0.7 &&
                (last.high - last.close) <= atr * 0.15;

            // 🔵 BUY LOGIC
            if (h1Bull || earlyBullStructure || impulseBull)
            {
                // 1 — Resistance Trap
                bool resistanceTrap = ResistanceTrapEngine.IsResistanceTrap(rates1H, midPrice, atr, SYMBOL);
                if (resistanceTrap)
                {
                    Console.WriteLine("❌ BUY blocked: Resistance trap detected");
                    LogFile.Write("❌ BUY blocked: Resistance trap detected");
                    return FilterResult.Block("❌ BUY blocked: Resistance trap");
                }

                if (earlyBullStructure)
                {
                    Console.WriteLine("⚡ Early bullish structure detected.");
                    LogFile.Write("⚡ Early bullish structure detected.");
                }

                if (impulseBull)
                {
                    Console.WriteLine("⚡ Bullish impulse candle detected.");
                    LogFile.Write("⚡ Bullish impulse candle detected.");
                }

                // 2 — Rejection candle
                if (RejectionFilters.IsBearishRejection(last, atr))
                {
                    Console.WriteLine("❌ BUY blocked: Bearish rejection");
                    LogFile.Write("❌ BUY blocked: Bearish rejection");
                    return FilterResult.Block("❌ BUY blocked: Bearish rejection");
                }

                // 3 — Weak breakout
                if (bullishBreakout && !RejectionFilters.IsStrongBullishBreakout(last, atr))
                {
                    Console.WriteLine("❌ BUY blocked: Weak bullish breakout");
                    LogFile.Write("❌ BUY blocked: Weak bullish breakout");
                    return FilterResult.Block("❌ BUY blocked: Weak bullish breakout");
                }

                // 4 — Momentum-based resistance check
                if (isNearHigh)
                {
                    if (momentumScore < dynamicThreshold)
                    {
                        Console.WriteLine($"❌ BUY blocked: Weak momentum near resistance (Score={momentumScore:F1})");
                        LogFile.Write($"❌ BUY blocked: Weak momentum near resistance (Score={momentumScore:F1})");
                        return FilterResult.Block("❌ BUY blocked: Weak momentum near resistance");
                    }

                    Console.WriteLine($"⚠ BUY allowed near resistance (momentum strong)");
                    LogFile.Write($"⚠ BUY allowed near resistance (momentum strong)");
                }

                if (isAtHigh)
                {
                    if (momentumScore < dynamicThreshold + 10)
                    {
                        Console.WriteLine("❌ BUY blocked: At resistance (insufficient momentum)");
                        LogFile.Write("❌ BUY blocked: At resistance (insufficient momentum)");
                        return FilterResult.Block("❌ BUY blocked: At resistance");
                    }

                    Console.WriteLine("⚠ BUY allowed at resistance (VERY strong momentum)");
                    LogFile.Write("⚠ BUY allowed at resistance (VERY strong momentum)");
                }

                if (isAboveHigh && !breakoutUpConfirmed)
                {
                    Console.WriteLine("❌ BUY blocked: Breakout not confirmed");
                    LogFile.Write("❌ BUY blocked: Breakout not confirmed");
                    return FilterResult.Block("❌ BUY blocked: Breakout not confirmed");
                }

                var adv = AdvancedResistance.CheckResistance(rates1H, midPrice, buffer, SYMBOL,
                                                                                    momentumScore,
                                                                                    dynamicThreshold);
                if (adv.Blocked) return adv;

                if (isAboveHigh && breakoutUpConfirmed)
                {
                    Console.WriteLine("BUY continuation: Confirmed breakout");
                    LogFile.Write("BUY continuation: Confirmed breakout");
                    return FilterResult.Allow();
                }

                if (!isNearHigh && !isAtHigh &&
                    TrendContinuation.IsBullTrendBuilding(rates1H, emaFast1H, emaSlow1H))
                {
                    Console.WriteLine("BUY OK: Trend continuation");
                    LogFile.Write("BUY OK: Trend continuation");
                    return FilterResult.Allow();
                }
            }

            // 🔴 SELL LOGIC
            if (h1Bear || earlyBearStructure || impulseBear)
            {
                // 1 — Support Trap
                bool supportTrap = SupportTrapEngine.IsSupportTrap(rates1H, midPrice, atr, SYMBOL);
                if (supportTrap)
                {
                    Console.WriteLine("❌ SELL blocked: Support trap detected");
                    LogFile.Write("❌ SELL blocked: Support trap detected");
                    return FilterResult.Block("❌ SELL blocked: Support trap");
                }

                if (earlyBearStructure)
                {
                    Console.WriteLine("⚡ Early bearish structure detected.");
                    LogFile.Write("⚡ Early bearish structure detected.");
                }

                if (impulseBear)
                {
                    Console.WriteLine("⚡ Bearish impulse candle detected.");
                    LogFile.Write("⚡ Bearish impulse candle detected.");
                }

                // 2 — Rejection candle
                if (RejectionFilters.IsBullishRejection(last, atr))
                {
                    Console.WriteLine("❌ SELL blocked: Bullish rejection");
                    LogFile.Write("❌ SELL blocked: Bullish rejection");
                    return FilterResult.Block("❌ SELL blocked: Bullish rejection");
                }

                // 3 — Weak breakout
                if (bearishBreakout && !RejectionFilters.IsStrongBearishBreakout(last, atr))
                {
                    Console.WriteLine("❌ SELL blocked: Weak bearish breakout");
                    LogFile.Write("❌ SELL blocked: Weak bearish breakout");
                    return FilterResult.Block("❌ SELL blocked: Weak bearish breakout");
                }

                // 4 — Momentum vs support
                if (isNearLow)
                {
                    if (momentumScore < dynamicThreshold)
                    {
                        Console.WriteLine($"❌ SELL blocked: Weak momentum near support (Score={momentumScore:F1})");
                        LogFile.Write($"❌ SELL blocked: Weak momentum near support (Score={momentumScore:F1})");
                        return FilterResult.Block("❌ SELL blocked: Weak momentum near support");
                    }

                    Console.WriteLine("⚠ SELL allowed near support (momentum strong)");
                    LogFile.Write("⚠ SELL allowed near support (momentum strong)");
                }

                if (isAtLow)
                {
                    if (momentumScore < dynamicThreshold + 10)
                    {
                        Console.WriteLine("❌ SELL blocked: At support (momentum too weak)");
                        LogFile.Write("❌ SELL blocked: At support (momentum too weak)");
                        return FilterResult.Block("❌ SELL blocked: At support");
                    }

                    Console.WriteLine("⚠ SELL allowed at support (VERY strong momentum)");
                    LogFile.Write("⚠ SELL allowed at support (VERY strong momentum)");
                }

                if (isBelowLow && !breakoutDownConfirmed)
                {
                    Console.WriteLine("❌ SELL blocked: Breakdown not confirmed");
                    LogFile.Write("❌ SELL blocked: Breakdown not confirmed");
                    return FilterResult.Block("❌ SELL blocked: Breakdown not confirmed");
                }

                var supp = AdvancedSupport.CheckSupport(rates1H, midPrice, buffer, SYMBOL);
                if (supp.Blocked) return supp;

                if (isBelowLow && breakoutDownConfirmed)
                {
                    Console.WriteLine("SELL continuation: Confirmed breakdown");
                    LogFile.Write("SELL continuation: Confirmed breakdown");
                    return FilterResult.Allow();
                }

                if (!isNearLow && !isAtLow &&
                    TrendContinuation.IsBearTrendBuilding(rates1H, emaFast1H, emaSlow1H))
                {
                    Console.WriteLine("SELL OK: Trend continuation");
                    LogFile.Write("SELL OK: Trend continuation");
                    return FilterResult.Allow();
                }
            }

            Console.WriteLine("FilterEngine: No block triggered → ALLOW");
            LogFile.Write("FilterEngine: No block triggered → ALLOW");
            return FilterResult.Allow();
        }
    }






}
