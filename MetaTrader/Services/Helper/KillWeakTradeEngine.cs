using MetaTrader.Data.Entities;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class KillWeakTradeEngine
    {
        public static bool ShouldExit(
            TrackedTrade t,
            double midPrice,
            double ema9,
            double ema21,
            double currentMomentum,
            double previousMomentum,
            MqlRates[] rates, double atr)
        {
            var c0 = rates[^1];
            var c1 = rates[^2];

            double moveFromEntry = Math.Abs(midPrice - t.EntryPrice);

            // --------------------------------------------------
            // 🧠 TREND PROTECTION (🔥 NEW - CRITICAL)
            // --------------------------------------------------
            bool strongTrend =
                (t.Side == TradeSide.Sell && ema9 < ema21) ||
                (t.Side == TradeSide.Buy && ema9 > ema21);

            bool smallCandle =
                Math.Abs(c0.close - c0.open) < atr * 0.5;

            if (strongTrend && smallCandle)
            {
                LogFile.Write($"🧠 Holding trade - trend continuation {t.Ticket}");
                return false;
            }

            // --------------------------------------------------
            // 🔴 1. NO FOLLOW-THROUGH
            // --------------------------------------------------
            bool noFollowThrough =
                moveFromEntry < atr * 0.5 &&   // 🔥 use ATR instead of fixed 3
                currentMomentum < previousMomentum;

            if (noFollowThrough && t.ProfitUsd < 3)
            {
                LogFile.Write($"⚠️ Weak trade (no follow-through) → EXIT {t.Ticket}");
                return true;
            }

            // --------------------------------------------------
            // 🔴 2. EMA REJECTION FAILURE
            // --------------------------------------------------
            bool emaFailure =
                t.Side == TradeSide.Sell
                    ? c0.close > ema9
                    : c0.close < ema9;

            if (emaFailure && t.ProfitUsd < 5)
            {
                LogFile.Write($"⚠️ EMA failure → EXIT {t.Ticket}");
                return true;
            }

            // --------------------------------------------------
            // 🔴 3. DOUBLE COUNTER CANDLES
            // --------------------------------------------------
            bool twoOppositeCandles =
                (t.Side == TradeSide.Sell &&
                 c0.close > c0.open &&
                 c1.close > c1.open)
                ||
                (t.Side == TradeSide.Buy &&
                 c0.close < c0.open &&
                 c1.close < c1.open);

            if (twoOppositeCandles && t.ProfitUsd < 5)
            {
                LogFile.Write($"⚠️ Double counter candles → EXIT {t.Ticket}");
                return true;
            }

            // --------------------------------------------------
            // 🔴 4. MOMENTUM COLLAPSE
            // --------------------------------------------------
            bool momentumCollapse =
                previousMomentum > 2 &&
                currentMomentum < 1;

            if (momentumCollapse && t.ProfitUsd < 5)
            {
                LogFile.Write($"⚠️ Momentum collapse → EXIT {t.Ticket}");
                return true;
            }

            return false;
        }
    }
}
