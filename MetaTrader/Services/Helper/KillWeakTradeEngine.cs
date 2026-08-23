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
            // 🧠 BASIC CANDLE / ATR DATA
            // --------------------------------------------------

            double body = Math.Abs(c0.close - c0.open);
            double bodyAtr = atr > 0 ? body / atr : 0;

            // --------------------------------------------------
            // 📉 ADVERSE MOVE
            // Positive only when price is moving against trade
            // --------------------------------------------------

            double adverseMove =
                t.Side == TradeSide.Buy
                    ? t.EntryPrice - midPrice
                    : midPrice - t.EntryPrice;

            double adverseAtr =
                atr > 0
                    ? Math.Max(0, adverseMove) / atr
                    : 0;

            //bool severeAdverseMove =
            
            //IsGold(t.Symbol) &&
            //t.ProfitUsd <= -5.00 &&
            //adverseAtr >= 0.45;

            //if (severeAdverseMove)
            //{
            //    LogFile.Write(
            //        $"🚨 Severe Gold adverse move → EXIT {t.Ticket} | " +
            //        $"profit={t.ProfitUsd:F2}, adverseAtr={adverseAtr:F2}"
            //    );

            //    return true;
            //}

            // --------------------------------------------------
            // 🔥 FAILED BREAKOUT DETECTION
            // --------------------------------------------------

            bool failedBreakout =
                (
                    t.Side == TradeSide.Buy &&
                    c0.close < t.EntryPrice &&
                    adverseAtr > 0.35 &&
                    currentMomentum < previousMomentum &&
                    bodyAtr > 0.70
                )
                ||
                (
                    t.Side == TradeSide.Sell &&
                    c0.close > t.EntryPrice &&
                    adverseAtr > 0.35 &&
                    currentMomentum < previousMomentum &&
                    bodyAtr > 0.70
                );

            // --------------------------------------------------
            // 💥 VIOLENT COUNTER-REVERSAL OVERRIDE
            // --------------------------------------------------

            double counterBodyRequirement =
                IsGold(t.Symbol) ? 0.65 :
                IsCrypto(t.Symbol) ? 0.80 :
                IsIndex(t.Symbol) ? 0.70 :
                0.60; // FX

            double counterAdverseRequirement =
                IsGold(t.Symbol) ? 0.30 :
                IsCrypto(t.Symbol) ? 0.35 :
                IsIndex(t.Symbol) ? 0.30 :
                0.25; // FX

            bool violentCounterReversal =
                (
                    t.Side == TradeSide.Sell &&
                    c0.close > c0.open &&
                    c0.close > t.EntryPrice &&
                    adverseAtr > counterAdverseRequirement &&
                    bodyAtr > counterBodyRequirement
                )
                ||
                (
                    t.Side == TradeSide.Buy &&
                    c0.close < c0.open &&
                    c0.close < t.EntryPrice &&
                    adverseAtr > counterAdverseRequirement &&
                    bodyAtr > counterBodyRequirement
                );

            if (violentCounterReversal && t.ProfitUsd < 0)
            {
                LogFile.Write(
                    $"💥 Violent counter reversal → EXIT {t.Ticket} | " +
                    $"symbol={t.Symbol}, profit={t.ProfitUsd:F2}, " +
                    $"adverseAtr={adverseAtr:F2}, " +
                    $"bodyAtr={bodyAtr:F2}"
                );

                return true;
            }

            // --------------------------------------------------
            // 🆕 EARLY LOSS FAILURE
            //
            // Prevents a steadily failing trade from reaching
            // the $10-$12 emergency stop simply because the
            // reversal was not violent enough.
            // --------------------------------------------------

            double earlyLossLimit =
                IsGold(t.Symbol) ? -5.00 :
                IsCrypto(t.Symbol) ? -4.00 :
                IsIndex(t.Symbol) ? -4.00 :
                -3.00; // FX

            bool priceFailedEma =
                t.Side == TradeSide.Buy
                    ? c0.close < ema9
                    : c0.close > ema9;

            bool earlyLossFailure =
                t.ProfitUsd <= earlyLossLimit &&
                adverseAtr >= 0.20 &&
                priceFailedEma;

            if (earlyLossFailure)
            {
                LogFile.Write(
                    $"🚨 Early loss failure → EXIT {t.Ticket} | " +
                    $"symbol={t.Symbol}, profit={t.ProfitUsd:F2}, " +
                    $"limit={earlyLossLimit:F2}, " +
                    $"adverseAtr={adverseAtr:F2}, " +
                    $"bodyAtr={bodyAtr:F2}"
                );

                return true;
            }

            // --------------------------------------------------
            // 🧠 STRONG TREND DETECTION
            // --------------------------------------------------

            double strongTrendMomentum =
                IsCrypto(t.Symbol) ? 1.8 :
                IsGold(t.Symbol) ? 1.6 :
                IsIndex(t.Symbol) ? 1.5 :
                1.4; // FX

            bool strongTrend =
                (
                    t.Side == TradeSide.Sell &&
                    ema9 < ema21 &&
                    currentMomentum > strongTrendMomentum
                )
                ||
                (
                    t.Side == TradeSide.Buy &&
                    ema9 > ema21 &&
                    currentMomentum > strongTrendMomentum
                );

            // --------------------------------------------------
            // 🛡 PROTECT STRONG TRADES
            // --------------------------------------------------

            if (strongTrend &&
                !failedBreakout &&
                !violentCounterReversal &&
                !earlyLossFailure &&
                t.ProfitUsd > -2)
            {
                LogFile.Write(
                    $"🛡 Strong trend → skip weak-exit logic {t.Ticket}"
                );

                return false;
            }

            // --------------------------------------------------
            // 🧠 SMALL CANDLE / TREND CONTINUATION
            // --------------------------------------------------

            bool smallCandle =
                body < atr * 0.50;

            if (strongTrend &&
                smallCandle &&
                !failedBreakout &&
                !violentCounterReversal &&
                !earlyLossFailure &&
                t.ProfitUsd > -2)
            {
                LogFile.Write(
                    $"🧠 Holding trade - trend continuation {t.Ticket}"
                );

                return false;
            }

            // --------------------------------------------------
            // 🔴 CONFIRMED WEAKNESS
            // --------------------------------------------------

            bool confirmedWeakness =
                (
                    t.Side == TradeSide.Sell &&
                    c0.close > c0.open &&
                    c1.close > c1.open
                )
                ||
                (
                    t.Side == TradeSide.Buy &&
                    c0.close < c0.open &&
                    c1.close < c1.open
                );

            // --------------------------------------------------
            // 🔥 FAILED BREAKOUT EXIT
            // --------------------------------------------------

            if (failedBreakout && t.ProfitUsd < 2)
            {
                LogFile.Write(
                    $"⚠️ Failed breakout detected → EXIT {t.Ticket}"
                );

                return true;
            }

            // --------------------------------------------------
            // 🔥 IMMEDIATE MOMENTUM FAILURE
            // --------------------------------------------------

            bool immediateMomentumFailure =
                moveFromEntry < atr * 0.25 &&
                adverseAtr > 0.20 &&
                currentMomentum < previousMomentum * 0.75;

            if (immediateMomentumFailure &&
                t.ProfitUsd < 2)
            {
                LogFile.Write(
                    $"⚠️ Immediate momentum failure → EXIT {t.Ticket}"
                );

                return true;
            }

            // --------------------------------------------------
            // 🔴 1. NO FOLLOW-THROUGH
            // --------------------------------------------------

            bool noFollowThrough =
                moveFromEntry < atr * 0.50 &&
                currentMomentum < previousMomentum;

            if (noFollowThrough &&
                confirmedWeakness &&
                t.ProfitUsd < 3)
            {
                LogFile.Write(
                    $"⚠️ Weak trade (confirmed no follow-through) → EXIT {t.Ticket}"
                );

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
                LogFile.Write(
                    $"⚠️ EMA failure (confirmed) → EXIT {t.Ticket}"
                );

                return true;
            }

            // --------------------------------------------------
            // 🔴 3. DOUBLE COUNTER CANDLES
            // --------------------------------------------------

            bool twoOppositeCandles =
                (
                    t.Side == TradeSide.Sell &&
                    c0.close > c0.open &&
                    c1.close > c1.open
                )
                ||
                (
                    t.Side == TradeSide.Buy &&
                    c0.close < c0.open &&
                    c1.close < c1.open
                );

            if (twoOppositeCandles &&
                t.ProfitUsd < 5)
            {
                LogFile.Write(
                    $"⚠️ Double counter candles → EXIT {t.Ticket}"
                );

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
                LogFile.Write(
                    $"⚠️ Momentum collapse (confirmed) → EXIT {t.Ticket}"
                );

                return true;
            }

            return false;
        }

        private static bool IsGold(string symbol)
        {
            string s =
                symbol?.ToUpperInvariant() ?? "";

            return
                s.Contains("XAU") ||
                s.Contains("XAG") ||
                s.Contains("GOLD") ||
                s.Contains("SILVER");
        }

        private static bool IsCrypto(string symbol)
        {
            string s =
                symbol?.ToUpperInvariant() ?? "";

            return
                s.Contains("BTC") ||
                s.Contains("ETH") ||
                s.Contains("SOL") ||
                s.Contains("BNB") ||
                s.Contains("XRP") ||
                s.Contains("ADA");
        }

        private static bool IsIndex(string symbol)
        {
            string s =
                symbol?.ToUpperInvariant() ?? "";

            return
                s.Contains("US100") ||
                s.Contains("NAS") ||
                s.Contains("US30") ||
                s.Contains("US500") ||
                s.Contains("SPX");
        }
    }
}
