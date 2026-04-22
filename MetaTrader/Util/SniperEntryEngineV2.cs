using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Util.Trading.Execution;
using MetaTrader.Services;
using MtApi5;
using System;
using System.Threading.Tasks;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Util
{
    public static class SniperEntryEngineV2
    {
        // =========================
        // Tunables (start here)
        // =========================
        public static double MinAtrPercentile = 0.60;         // 60%+ (filters dead markets)
        public static int StructureLookback = 3;              // local break level
        public static double ImpulseRangeAtr = 1.10;          // impulse candle must be >= 1.10 ATR
        public static double ImpulseBodyRatio = 0.55;         // body/range must be >= 55%

        public static double PullbackMaxAtr = 0.60;           // pullback candle range <= 0.60 ATR
        public static double PullbackWickRatioMax = 0.65;     // avoid messy wick bombs
        public static double PullbackNearEmaAtr = 0.35;       // price close to ema9 within 0.35 ATR

        public static double ContinuationRangeAtr = 0.70;     // continuation candle range >= 0.70 ATR
        public static double ContinuationBodyRatio = 0.55;    // continuation must be strong

        public static double EmaDistanceAtrMin = 0.15;        // ema9-ema21 distance must be >= 0.15 ATR
        public static double MaxSpreadAtrFactor = 0.25;       // spread cap

        public static TradeAction Decide(
            string symbol,
            MtApi5Client client,
            MqlRates[] ratesH1,
            double atr,
            double atrPercentile,
            double[] emaFastH1,
            double[] emaSlowH1,
            SymbolContext ctx,
            M15ConfirmationService M15Service,
            out SniperEvent ev)
        {
            ev = new SniperEvent
            {
                Regime = "Hold",
                ActionDescription = "No setup",
                SniperAction = TradeAction.Hold.ToString()
            };

            // -----------------------------
            // 0) Safety
            // -----------------------------
            if (ratesH1 == null || ratesH1.Length < 10) return TradeAction.Hold;
            if (emaFastH1 == null || emaFastH1.Length < 3) return TradeAction.Hold;
            if (emaSlowH1 == null || emaSlowH1.Length < 3) return TradeAction.Hold;
            if (atr <= 0) return TradeAction.Hold;

            // -----------------------------
            // 1) State guard: Armed only
            // -----------------------------
            if (ctx.State != MarketState.Armed)
            {
                ev.Regime = "NotArmed";
                ev.ActionDescription = "Not armed";
                return TradeAction.Hold;
            }

            // -----------------------------
            // 2) ATR percentile guard
            // -----------------------------
            if (atrPercentile < MinAtrPercentile)
            {
                ev.Regime = "LowATR";
                ev.ActionDescription = $"ATR percentile too low ({atrPercentile:P0})";
                return TradeAction.Hold;
            }

            // -----------------------------
            // 3) Spread guard (ATR relative)
            // -----------------------------
            if (ExecutionGuards.IsSpreadTooWide(client, symbol, atr, MaxSpreadAtrFactor))
            {
                ev.Regime = "SpreadBlock";
                ev.ActionDescription = "Spread too wide vs ATR";
                return TradeAction.Hold;
            }

            // -----------------------------
            // 4) Shock guard (don’t trade shocks)
            // -----------------------------
            var lastClosed = ratesH1[^1]; // NOTE: Ensure your caller passes H1 with shift=1 (closed candles)
            if (VolatilityGuard.IsShockBlocking(symbol, lastClosed.time, atr))
            {
                ev.Regime = "ShockBlock";
                ev.ActionDescription = "Blocked due to recent shock";
                return TradeAction.Hold;
            }

            // -----------------------------
            // 5) Trend alignment (EMA9/EMA21)
            // -----------------------------
            bool bullish = emaFastH1[^1] > emaSlowH1[^1] && emaFastH1[^1] > emaFastH1[^2];
            bool bearish = emaFastH1[^1] < emaSlowH1[^1] && emaFastH1[^1] < emaFastH1[^2];

            if (!bullish && !bearish)
            {
                ev.Regime = "NoTrend";
                ev.ActionDescription = "EMA trend not aligned";
                return TradeAction.Hold;
            }

            // -----------------------------
            // 6) Strong-trend filter (avoid weak slopes)
            // -----------------------------
            double emaDist = Math.Abs(emaFastH1[^1] - emaSlowH1[^1]);
            if (emaDist < atr * EmaDistanceAtrMin)
            {
                ev.Regime = "WeakTrend";
                ev.ActionDescription = "EMA distance too small (chop risk)";
                return TradeAction.Hold;
            }

            // ==========================================================
            // ✅ V2 ENTRY PATTERN (win-rate friendly)
            // Breakout/Impulse (C-2) → Pullback (C-1) → Continuation (C0)
            // ==========================================================

            var c0 = ratesH1[^1];  // most recent closed
            var c1 = ratesH1[^2];
            var c2 = ratesH1[^3];

            // 7) Impulse candle (c2) must be real
            bool impulseOk = IsImpulse(c2, atr, bullish ? TradeDirection.Bullish : TradeDirection.Bearish);

            if (!impulseOk)
            {
                ev.Regime = "NoImpulse";
                ev.ActionDescription = "No qualifying impulse candle (C-2)";
                return TradeAction.Hold;
            }

            // 8) Pullback candle (c1) must be controlled near EMA9
            double ema9 = emaFastH1[^1];

            bool pullbackOk = IsControlledPullback(c1, atr, ema9);

            if (!pullbackOk)
            {
                ev.Regime = "BadPullback";
                ev.ActionDescription = "Pullback not controlled / not near EMA9";
                return TradeAction.Hold;
            }

            // 9) Continuation candle (c0) must confirm direction
            bool continuationOk = IsContinuation(c0, atr, bullish ? TradeDirection.Bullish : TradeDirection.Bearish);

            if (!continuationOk)
            {
                ev.Regime = "NoContinuation";
                ev.ActionDescription = "No continuation confirmation (C0)";
                return TradeAction.Hold;
            }

            // 10) Optional: structure break check using local highs/lows
            bool brokeStructure = BreaksStructure(ratesH1, c0, bullish ? TradeDirection.Bullish : TradeDirection.Bearish);
            if (!brokeStructure)
            {
                ev.Regime = "NoStructureBreak";
                ev.ActionDescription = "Continuation did not break local structure";
                return TradeAction.Hold;
            }

            // ✅ PASS so far: pattern valid
            var action = bullish ? TradeAction.EnterBuy : TradeAction.EnterSell;

            // --------------------------------------------------
            // 11) M15 CONFIRMATION
            // --------------------------------------------------
            TradeAction direction =
                action == TradeAction.EnterBuy
                    ? TradeAction.Bullish
                    : TradeAction.Bearish;
            var reason = "";
            bool m15Confirmed = M15Service.IsTradeAllowed(symbol, direction, EntryModel.Sniper, ctx.CurrentMomentum, out reason);

            if (!m15Confirmed)
            {
                ev.Regime = "M15Blocked";
                ev.ActionDescription = "⛔ Entry blocked — M15 not aligned";
                ev.SniperAction = TradeAction.Hold.ToString();

                return TradeAction.Hold;
            } 

            // --------------------------------------------------
            // ✅ FINAL CONFIRMED ENTRY
            // --------------------------------------------------
            ev.Regime = "ContinuationEntry";
            ev.ActionDescription = "Impulse → Pullback → Continuation + M15 confirmed";
            ev.SniperAction = action.ToString();

            return action;

        }

        // =========================
        // Helpers
        // =========================

        private static bool IsImpulse(MqlRates c, double atr, TradeDirection dir)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double bodyRatio = body / range;

            bool directionOk = dir == TradeDirection.Bullish ? c.close > c.open : c.close < c.open;
            bool bigRange = range >= atr * ImpulseRangeAtr;
            bool strongBody = bodyRatio >= ImpulseBodyRatio;

            return directionOk && bigRange && strongBody;
        }

        private static bool IsControlledPullback(MqlRates c, double atr, double ema9)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double upperWick = c.high - Math.Max(c.open, c.close);
            double lowerWick = Math.Min(c.open, c.close) - c.low;
            double wickRatio = (upperWick + lowerWick) / range;

            bool notTooBig = range <= atr * PullbackMaxAtr;
            bool notWicky = wickRatio <= PullbackWickRatioMax;

            double mid = (c.open + c.close) / 2.0;
            bool nearEma = Math.Abs(mid - ema9) <= atr * PullbackNearEmaAtr;

            return notTooBig && notWicky && nearEma;
        }

        private static bool IsContinuation(MqlRates c, double atr, TradeDirection dir)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double bodyRatio = body / range;

            bool directionOk = dir == TradeDirection.Bullish ? c.close > c.open : c.close < c.open;
            bool enoughRange = range >= atr * ContinuationRangeAtr;
            bool strongBody = bodyRatio >= ContinuationBodyRatio;

            return directionOk && enoughRange && strongBody;
        }

        private static bool BreaksStructure(MqlRates[] rates, MqlRates c0, TradeDirection dir)
        {
            // Look back StructureLookback candles *before* c0
            // We assume rates[^1]=c0, so we examine [^2..]
            int n = StructureLookback;
            if (rates.Length < n + 5) return true; // fail-open if not enough history

            double hh = double.MinValue;
            double ll = double.MaxValue;

            for (int i = 2; i < 2 + n; i++)
            {
                var c = rates[^i];
                hh = Math.Max(hh, c.high);
                ll = Math.Min(ll, c.low);
            }

            return dir == TradeDirection.Bullish
                ? c0.close > hh
                : c0.close < ll;
        }
    }

}
