using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;

namespace MetaTrader.Services
{
    public static class CascadeBreakdownEngine
    {
        public static double BreakRangeAtr = 0.70;     // reduced
        public static double CascadeRangeAtr = 0.90;   // reduced
        public static double BodyRatio = 0.50;         // reduced

        public static bool ShouldEnter(
            string symbol,
            MtApi5Client client,
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

            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            var c0 = rates[^1];
            var c1 = rates[^2];
            var c2 = rates[^3];

            // --------------------------------
            // TREND (keep simple)
            // --------------------------------
            bool trendDown = emaFast[^1] < emaSlow[^1];
            bool trendUp = emaFast[^1] > emaSlow[^1];

            // --------------------------------
            // BREAKOUT DETECTION (relaxed)
            // --------------------------------
            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);
            double ratio0 = body0 / Math.Max(range0, 0.00001);

            bool breakout =
                range0 >= atr * BreakRangeAtr &&
                ratio0 >= BodyRatio;

            if (!breakout)
                return false;

            // --------------------------------
            // FOLLOW-THROUGH (relaxed)
            // --------------------------------
            double range1 = c1.high - c1.low;

            bool followThrough =
                range1 >= atr * (BreakRangeAtr * 0.8);

            // --------------------------------
            // LIVE BREAK
            // --------------------------------
            bool breakDown = bid < c2.low;
            bool breakUp = ask > c2.high;

            // --------------------------------
            // MOMENTUM (relaxed)
            // --------------------------------
            double prevMove = Math.Abs(c1.close - c1.open);
            double moveSell = Math.Abs(bid - c0.open);
            double moveBuy = Math.Abs(ask - c0.open);

            bool momentumSell = moveSell >= prevMove * 0.6;
            bool momentumBuy = moveBuy >= prevMove * 0.6;

            // --------------------------------
            // CASCADE CONDITIONS
            // --------------------------------
            bool bearishCascade =
                breakDown &&
                trendDown &&
                breakout &&
                followThrough &&
                momentumSell;

            bool bullishCascade =
                breakUp &&
                trendUp &&
                breakout &&
                followThrough &&
                momentumBuy;

            if (bearishCascade)
            {
                LogFile.Write($"[{symbol}] ⚡ Cascade SELL");
                action = TradeAction.EnterSell;
                return true;
            }

            if (bullishCascade)
            {
                LogFile.Write($"[{symbol}] ⚡ Cascade BUY");
                action = TradeAction.EnterBuy;
                return true;
            }

            return false;
        }
    }
}
