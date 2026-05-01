using AutoMapper;
using MetaTrader.Enums;
using MetaTrader.Services.Filter;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Buffers;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MetaTrader.Services
{
    public static class TrendRideContinuationEngine
    {
        public static double PullbackAtrFactor = 0.7;
        public static double MaxPullbackAtr = 1.5;
        public static double BodyStrength = 0.45;

        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double atrPercentile,
            SymbolContext ctx,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < 6 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return false;

            var c0 = rates[^2]; // latest closed
            var c1 = rates[^3]; // pullback

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];

            // =============================
            // TREND
            // =============================
            bool trendUp =
                emaFast[^2] > emaSlow[^2] &&
                emaFast[^2] > emaFast[^3];

            bool trendDown =
                emaFast[^2] < emaSlow[^2] &&
                emaFast[^2] < emaFast[^3];

            if (!trendUp && !trendDown)
                return false;

            double emaSlope = emaFast[^2] - emaFast[^3];
            bool strongTrend = Math.Abs(emaSlope) > atr * 0.05;

            // =============================
            // ATR REGIME (RELAXED)
            // =============================
            if (atrPercentile < 0.40)
                return false;

            // =============================
            // EXTENSION (SMART)
            // =============================
            double distanceFromEMA9 = Math.Abs(c0.close - ema9);

            double maxDistance =
                symbol.Contains("XAU") ? 1.2 :
                symbol.Contains("BTC") || symbol.Contains("ETH") ? 1.4 :
                0.8;

            if (!strongTrend && distanceFromEMA9 > atr * maxDistance)
                return false;

            // =============================
            // PULLBACK (RELAXED)
            // =============================
            double tolerance = atr * PullbackAtrFactor;

            bool pullbackNearFast =
                Math.Abs(c1.close - emaFast[^3]) <= tolerance;

            bool shallowPullback =
                Math.Abs(c1.close - emaFast[^3]) <= atr * 1.0;

            bool validPullback = pullbackNearFast || shallowPullback;

            // =============================
            // DEPTH CHECK
            // =============================
            double distToSlow = Math.Abs(c1.close - emaSlow[^3]);

            if (distToSlow > atr * MaxPullbackAtr)
                return false;

            // =============================
            // CONTINUATION CANDLE
            // =============================
            double range = c0.high - c0.low;
            if (range <= 0) return false;

            double body = Math.Abs(c0.close - c0.open);
            double bodyRatio = body / range;

            bool bullishCandle =
                c0.close > c0.open &&
                bodyRatio >= BodyStrength;

            bool bearishCandle =
                c0.close < c0.open &&
                bodyRatio >= BodyStrength;

            // =============================
            // ENTRY
            // =============================
            if (trendUp && validPullback && bullishCandle)
            {
                LogFile.Write($"[{symbol}] TrendRide BUY");
                action = TradeAction.EnterBuy;
                return true;
            }

            if (trendDown && validPullback && bearishCandle)
            {
                LogFile.Write($"[{symbol}] TrendRide SELL");
                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }
    }
}
