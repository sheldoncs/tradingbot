using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class EmaPullbackContinuationEngineV2
    {
        // =========================
        // Tunables
        // =========================
        public static double MinAtrPercentile = 0.55;

        public static double PullbackToFastAtr = 0.45;
        public static double PullbackToSlowAtr = 1.20;

        public static double MaxPullbackBodyAtr = 0.65;
        public static double MaxPullbackWickRatio = 0.80;

        public static double ContinuationBodyRatio = 0.45;
        public static double ContinuationRangeAtr = 0.45;

        public static double TrendSlopeMinFactor = 0.03;
        public static double BreakBufferAtr = 0.05;

        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double atrPercentile,
            double[] emaFast,
            double[] emaSlow,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < 6 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return false;

            if (atrPercentile < MinAtrPercentile)
                return false;

            // --------------------------------
            // CLOSED CANDLES ONLY
            // newest candle is at the end in your bot
            // --------------------------------
            var c0 = rates[^1]; // continuation candle
            var c1 = rates[^2]; // pullback candle
            var c2 = rates[^3]; // trend candle before pullback
            var c3 = rates[^4]; // earlier trend candle

            double ema9Now = emaFast[^1];
            double ema9Prev = emaFast[^2];
            double ema21Now = emaSlow[^1];
            double ema21Prev = emaSlow[^2];

            // --------------------------------
            // TREND FILTER
            // --------------------------------
            bool bullishTrend =
                ema9Now > ema21Now &&
                ema9Now > ema9Prev;

            bool bearishTrend =
                ema9Now < ema21Now &&
                ema9Now < ema9Prev;

            if (!bullishTrend && !bearishTrend)
                return false;

            // --------------------------------
            // EMA SLOPE FILTER
            // --------------------------------
            double fastSlope = emaFast[^1] - emaFast[^2];
            double slowSlope = emaSlow[^1] - emaSlow[^2];

            if (bullishTrend)
            {
                bool slopeWeak = fastSlope < atr * TrendSlopeMinFactor;

                bool earlyTurn =
                    ema9Now > ema21Now &&     // just crossed / above
                    ema9Prev <= ema21Prev;    // was below before

                if (slopeWeak && !earlyTurn)
                {
                    LogFile.Write($"[{symbol}] Pullback blocked: weak slope");
                    return false;
                }
            }

            if (bearishTrend)
            {
                bool slopeWeak = fastSlope > -atr * TrendSlopeMinFactor;

                bool earlyTurn =
                    ema9Now < ema21Now &&
                    ema9Prev >= ema21Prev;

                if (slopeWeak && !earlyTurn)
                {
                    LogFile.Write($"[{symbol}] Pullback blocked: weak slope");
                    return false;
                }
            }

            // --------------------------------
            // PULLBACK LOCATION
            // c1 should pull back toward EMA9
            // but not too deep beyond EMA21
            // --------------------------------
            bool nearFastForBuy =
                Math.Abs(c1.low - emaFast[^2]) <= atr * PullbackToFastAtr ||
                Math.Abs(c1.close - emaFast[^2]) <= atr * PullbackToFastAtr;

            bool nearFastForSell =
                Math.Abs(c1.high - emaFast[^2]) <= atr * PullbackToFastAtr ||
                Math.Abs(c1.close - emaFast[^2]) <= atr * PullbackToFastAtr;

            bool notTooDeepBuy =
                Math.Abs(c1.low - emaSlow[^2]) <= atr * PullbackToSlowAtr ||
                c1.low >= emaSlow[^2] - atr * PullbackToSlowAtr;

            bool notTooDeepSell =
                Math.Abs(c1.high - emaSlow[^2]) <= atr * PullbackToSlowAtr ||
                c1.high <= emaSlow[^2] + atr * PullbackToSlowAtr;

            // --------------------------------
            // PULLBACK HEALTH
            // c1 should be a controlled counter-trend candle
            // --------------------------------
            double c1Range = c1.high - c1.low;
            if (c1Range <= 0)
                return false;

            double c1Body = Math.Abs(c1.close - c1.open);
            double c1UpperWick = c1.high - Math.Max(c1.open, c1.close);
            double c1LowerWick = Math.Min(c1.open, c1.close) - c1.low;
            double c1WickRatio = (c1UpperWick + c1LowerWick) / c1Range;

            bool controlledPullback =
                c1Body <= atr * MaxPullbackBodyAtr &&
                c1WickRatio <= MaxPullbackWickRatio;

            if (!controlledPullback)
                return false;

            // --------------------------------
            // CONTINUATION CANDLE QUALITY
            // c0 must reassert trend direction
            // --------------------------------
            double c0Range = c0.high - c0.low;
            if (c0Range <= 0)
                return false;

            double c0Body = Math.Abs(c0.close - c0.open);
            double c0BodyRatio = c0Body / c0Range;

            bool bullishContinuationCandle =
                c0.close > c0.open &&
                c0BodyRatio >= ContinuationBodyRatio &&
                c0Range >= atr * ContinuationRangeAtr;

            bool bearishContinuationCandle =
                c0.close < c0.open &&
                c0BodyRatio >= ContinuationBodyRatio &&
                c0Range >= atr * ContinuationRangeAtr;

            // --------------------------------
            // STRUCTURE CONFIRMATION
            // continuation candle should break pullback structure
            // --------------------------------
            double breakBuffer = atr * BreakBufferAtr;

            bool buyBreaksPullback =
                c0.high > c1.high + breakBuffer ||
                c0.close > c1.high;

            bool sellBreaksPullback =
                c0.low < c1.low - breakBuffer ||
                c0.close < c1.low;

            // --------------------------------
            // TREND LEG CONFIRMATION
            // before the pullback, market should already be moving
            // --------------------------------
            bool priorBullLeg =
                c2.close > c3.close &&
                c2.high >= c3.high;

            bool priorBearLeg =
                c2.close < c3.close &&
                c2.low <= c3.low;

            // --------------------------------
            // BUY
            // --------------------------------
            if (bullishTrend &&
                priorBullLeg &&
                nearFastForBuy &&
                notTooDeepBuy &&
                bullishContinuationCandle &&
                buyBreaksPullback)
            {
                LogFile.Write($"[{symbol}] EMA Pullback Continuation BUY");
                action = TradeAction.EnterBuy;
                return true;
            }

            // --------------------------------
            // SELL
            // --------------------------------
            if (bearishTrend &&
                priorBearLeg &&
                nearFastForSell &&
                notTooDeepSell &&
                bearishContinuationCandle &&
                sellBreaksPullback)
            {
                LogFile.Write($"[{symbol}] EMA Pullback Continuation SELL");
                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }
    }
}
