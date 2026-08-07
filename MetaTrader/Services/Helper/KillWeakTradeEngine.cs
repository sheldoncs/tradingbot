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
            MqlRates[] rates,
            double atr)
        {
            var c0 = rates[^1];
            var c1 = rates[^2];

            double moveFromEntry = Math.Abs(midPrice - t.EntryPrice);



            // --------------------------------------------------
            // 🧠 TREND PROTECTION
            // --------------------------------------------------

            double body = Math.Abs(c0.close - c0.open);
            double bodyAtr = atr > 0 ? body / atr : 0;

            // --------------------------------------------------
            // 🔥 FAILED BREAKOUT DETECTION (Improved)
            // Works on Gold, Crypto, FX and Indices
            // --------------------------------------------------

            double adverseMove =
                t.Side == TradeSide.Buy
                    ? t.EntryPrice - midPrice
                    : midPrice - t.EntryPrice;

            bool failedBreakout =
            (
                t.Side == TradeSide.Buy &&
                c0.close < t.EntryPrice &&
                adverseMove > atr * 0.35 &&
                currentMomentum < previousMomentum &&
                bodyAtr > 0.7
            )
            ||
            (
                t.Side == TradeSide.Sell &&
                c0.close > t.EntryPrice &&
                adverseMove > atr * 0.35 &&
                currentMomentum < previousMomentum &&
                bodyAtr > 0.7
            );

            double strongTrendMomentum =
                IsCrypto(t.Symbol) ? 1.8 :
                IsGold(t.Symbol) ? 1.6 :
                IsIndex(t.Symbol) ? 1.5 :
                1.4;   // FX

            bool strongTrend =
                (t.Side == TradeSide.Sell &&
                 ema9 < ema21 &&
                 currentMomentum > strongTrendMomentum)
            ||
                (t.Side == TradeSide.Buy &&
                 ema9 > ema21 &&
                 currentMomentum > strongTrendMomentum);

            // 🔥 PROTECT STRONG TRADES
            if (strongTrend &&
                 !failedBreakout &&
                 t.ProfitUsd > -2)
            {
                LogFile.Write($"🛡 Strong trend → skip weak-exit logic {t.Ticket}");
                return false;
            }

            bool smallCandle =
                Math.Abs(c0.close - c0.open) < atr * 0.5;

            if (strongTrend && smallCandle)
            {
                LogFile.Write($"🧠 Holding trade - trend continuation {t.Ticket}");
                return false;
            }

            // --------------------------------------------------
            // 🔴 CONFIRMED WEAKNESS
            // --------------------------------------------------

            bool confirmedWeakness =
                (t.Side == TradeSide.Sell &&
                 c0.close > c0.open &&
                 c1.close > c1.open)
                ||
                (t.Side == TradeSide.Buy &&
                 c0.close < c0.open &&
                 c1.close < c1.open);

            // --------------------------------------------------
            // 🔥 NEW - FAILED BREAKOUT DETECTION
            // --------------------------------------------------


            if (failedBreakout && t.ProfitUsd < 2)
            {
                LogFile.Write($"⚠️ Failed breakout detected → EXIT {t.Ticket}");
                return true;
            }

            // --------------------------------------------------
            // 🔥 NEW - IMMEDIATE MOMENTUM FAILURE
            // --------------------------------------------------

            bool immediateMomentumFailure =
                moveFromEntry < atr * 0.25 &&
                adverseMove > atr * 0.20 &&
                currentMomentum < previousMomentum * 0.75;

            if (immediateMomentumFailure && t.ProfitUsd < 2)
            {
                LogFile.Write($"⚠️ Immediate momentum failure → EXIT {t.Ticket}");
                return true;
            }

            // --------------------------------------------------
            // 🔴 1. NO FOLLOW-THROUGH
            // --------------------------------------------------

            bool noFollowThrough =
                moveFromEntry < atr * 0.5 &&
                currentMomentum < previousMomentum;

            if (noFollowThrough &&
                confirmedWeakness &&
                t.ProfitUsd < 3)
            {
                LogFile.Write($"⚠️ Weak trade (confirmed no follow-through) → EXIT {t.Ticket}");
                return true;
            }

            // --------------------------------------------------
            // 🔴 2. EMA FAILURE
            // --------------------------------------------------

            bool emaFailure =
                t.Side == TradeSide.Sell
                    ? c0.close > ema9
                    : c0.close < ema9;

            if (emaFailure &&
                confirmedWeakness &&
                t.ProfitUsd < 5)
            {
                LogFile.Write($"⚠️ EMA failure (confirmed) → EXIT {t.Ticket}");
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

            if (twoOppositeCandles &&
                t.ProfitUsd < 5)
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

            if (momentumCollapse &&
                confirmedWeakness &&
                t.ProfitUsd < 5)
            {
                LogFile.Write($"⚠️ Momentum collapse (confirmed) → EXIT {t.Ticket}");
                return true;
            }

            return false;
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") || symbol.Contains("XAG");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("SPX");
        }
    }
}
