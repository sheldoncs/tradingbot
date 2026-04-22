using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;

namespace MetaTrader.Services
{
    public static class CascadeBreakdownEngine
    {
        public static double BreakRangeAtr = 0.85;
        public static double CascadeRangeAtr = 1.10;
        public static double BodyRatio = 0.58;

        public static bool ShouldEnter(
            string symbol,
            MtApi5Client client, // 🔥 NEW (for bid/ask)
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < 6 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 3 || emaSlow.Length < 3 ||
                atr <= 0)
                return false;

            // --------------------------------
            // LIVE PRICE (CRITICAL ADD)
            // --------------------------------
            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            // --------------------------------
            // CANDLES (STRUCTURE ONLY)
            // --------------------------------
            var c0 = rates[^1];
            var c1 = rates[^2];
            var c2 = rates[^3];
            var c3 = rates[^4];

            // --------------------------------
            // PRIOR COMPRESSION
            // --------------------------------
            bool wasCompressed = CompressionDetector.IsCompressed(
                symbol,
                rates.Take(rates.Length - 2).ToArray(),
                atr);

            // --------------------------------
            // STRONG BREAKOUT DETECTION
            // --------------------------------
            double range = c0.high - c0.low;
            double body = Math.Abs(c0.close - c0.open);
            double ratio = body / Math.Max(range, 0.00001);

            bool strongBreakout =
                range >= atr * 1.2 &&
                ratio >= 0.55;

            if (!wasCompressed && !strongBreakout)
            {
                LogFile.Write($"[{symbol}] FX cascade blocked: no compression or breakout");
                return false;
            }

            // --------------------------------
            // LIQUIDITY SWEEP
            // --------------------------------
            bool bearishSweep = LiquiditySweepDetector.IsBearishSweep(rates);
            bool bullishSweep = LiquiditySweepDetector.IsBullishSweep(rates);

            // --------------------------------
            // TREND
            // --------------------------------
            bool trendDown = emaFast[^1] < emaSlow[^1];
            bool trendUp = emaFast[^1] > emaSlow[^1];

            // --------------------------------
            // 🔥 LIVE EMA DISTANCE (FIXED)
            // --------------------------------
            double ema = emaFast[^1];

            double distSell = Math.Abs(bid - ema);
            double distBuy = Math.Abs(ask - ema);

            if (distSell > atr * 1.2 && distBuy > atr * 1.2)
            {
                LogFile.Write($"[{symbol}] FX cascade blocked: too extended (live)");
                return false;
            }

            // --------------------------------
            // RANGE + BODY (STRUCTURE)
            // --------------------------------
            double range0 = c0.high - c0.low;
            double range1 = c1.high - c1.low;

            if (range0 <= 0 || range1 <= 0)
                return false;

            double body0 = Math.Abs(c0.close - c0.open);
            double body1 = Math.Abs(c1.close - c1.open);

            double ratio0 = body0 / range0;
            double ratio1 = body1 / range1;

            // --------------------------------
            // FOLLOW-THROUGH
            // --------------------------------
            bool expansionStack =
                range1 >= atr * BreakRangeAtr &&
                range0 >= range1 * 0.95;

            if (!expansionStack)
            {
                LogFile.Write($"[{symbol}] FX cascade blocked: weak follow-through");
                return false;
            }

            // --------------------------------
            // 🔥 LIVE BREAK CONFIRMATION (NEW)
            // --------------------------------
            bool liveBreakDown = bid < c2.low;
            bool liveBreakUp = ask > c2.high;

            // --------------------------------
            // 🔥 MOMENTUM CONTINUATION CHECK (NEW)
            // Prevent late entries
            // --------------------------------
            double prevMove = Math.Abs(c1.close - c1.open);

            double moveSell = Math.Abs(bid - c0.open);
            double moveBuy = Math.Abs(ask - c0.open);

            bool momentumSell = moveSell >= prevMove * 0.8;
            bool momentumBuy = moveBuy >= prevMove * 0.8;

            // --------------------------------
            // CASCADE CONDITIONS (UPDATED)
            // --------------------------------
            bool bearishCascade =
                liveBreakDown &&                     // 🔥 LIVE break
                range1 >= atr * BreakRangeAtr &&
                range0 >= atr * CascadeRangeAtr &&
                ratio1 >= BodyRatio &&
                ratio0 >= BodyRatio &&
                trendDown &&
                bearishSweep &&
                momentumSell;

            bool bullishCascade =
                liveBreakUp &&
                range1 >= atr * BreakRangeAtr &&
                range0 >= atr * CascadeRangeAtr &&
                ratio1 >= BodyRatio &&
                ratio0 >= BodyRatio &&
                trendUp &&
                bullishSweep &&
                momentumBuy;

            // --------------------------------
            // FINAL
            // --------------------------------
            if (bearishCascade)
            {
                LogFile.Write($"[{symbol}] 📉 FX Cascade SELL (LIVE)");
                action = TradeAction.EnterSell;
                return true;
            }

            if (bullishCascade)
            {
                LogFile.Write($"[{symbol}] 📈 FX Cascade BUY (LIVE)");
                action = TradeAction.EnterBuy;
                return true;
            }

            return false;
        }
    }
}
