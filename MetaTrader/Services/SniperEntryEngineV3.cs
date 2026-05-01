using MetaTrader.Data.Database;
using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.Contract;
using MetaTrader.Util;
using MetaTrader.Util.Trading.Execution;
using MtApi5;
using System;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Services
{
    public static class SniperEntryEngineV3
    {
        public static double MinAtrPercentile = 0.40;
        public static int StructureLookback = 3;

        public static double ImpulseRangeAtr = 0.80;
        public static double ImpulseBodyRatio = 0.50;

        public static double PullbackMaxAtr = 0.95;
        public static double PullbackWickRatioMax = 0.80;
        public static double PullbackNearEmaAtr = 0.80;

        public static double ContinuationRangeAtr = 0.50;
        public static double ContinuationBodyRatio = 0.45;

        public static double EmaDistanceAtrMin = 0.08;
        public static double MaxSpreadAtrFactor = 0.30;

        const int MinScore = 5;

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
            ITradingPairProperties _tradingPairProperties,
            out SniperEvent ev)
        {
            ev = new SniperEvent
            {
                Regime = "Hold",
                ActionDescription = "No setup",
                SniperAction = TradeAction.Hold.ToString()
            };

            int score = 0;

            try
            {
                if (ratesH1 == null || ratesH1.Length < 10) return TradeAction.Hold;
                if (emaFastH1 == null || emaFastH1.Length < 3) return TradeAction.Hold;
                if (emaSlowH1 == null || emaSlowH1.Length < 3) return TradeAction.Hold;
                if (atr <= 0) return TradeAction.Hold;

                if (ctx.State != MarketState.Armed) return TradeAction.Hold;
                if (atrPercentile < MinAtrPercentile) return TradeAction.Hold;

                if (ExecutionGuards.IsSpreadTooWide(client, symbol, atr, MaxSpreadAtrFactor))
                    return TradeAction.Hold;

                var c0 = ratesH1[^1];
                var c1 = ratesH1[^2];
                var c2 = ratesH1[^3];

                if (VolatilityGuard.IsShockBlocking(symbol, c0.time, atr))
                    return TradeAction.Hold;

                // =========================
                // TREND
                // =========================
                bool bullish = emaFastH1[^1] > emaSlowH1[^1];
                bool bearish = emaFastH1[^1] < emaSlowH1[^1];

                if (!bullish && !bearish)
                    return TradeAction.Hold;

                double emaSlope = emaFastH1[^1] - emaFastH1[^2];
                bool slopeOk = bullish ? emaSlope > 0 : emaSlope < 0;

                if (!slopeOk)
                    return TradeAction.Hold;

                double emaDist = Math.Abs(emaFastH1[^1] - emaSlowH1[^1]);
                if (emaDist < atr * EmaDistanceAtrMin)
                    return TradeAction.Hold;

                // =========================
                // EMA EXTENSION (SMART)
                // =========================
                double ema9 = emaFastH1[^1];
                double distanceFromEma = Math.Abs(c0.close - ema9);

                double maxDistanceAtr =
                    symbol.Contains("XAU") ? 1.1 :
                    symbol.Contains("US100") ? 1.0 :
                    symbol.Contains("ETH") || symbol.Contains("BTC") ? 1.2 :
                    0.7;

                bool strongTrend = Math.Abs(emaSlope) > atr * 0.05;

                bool tooExtended = !strongTrend && distanceFromEma > atr * maxDistanceAtr;

                if (tooExtended)
                {
                    LogFile.Write($"[SNIPER] {symbol} blocked: too extended (non-trend)");
                    return TradeAction.Hold;
                }

                // =========================
                // PRIOR IMPULSE (RELAXED)
                // =========================
                double prevRange = c1.high - c1.low;

                if (prevRange > atr * 1.4)
                {
                    LogFile.Write($"[SNIPER] {symbol} blocked: extreme impulse");
                    return TradeAction.Hold;
                }

                // =========================
                // STRUCTURE SCORING
                // =========================
                bool impulseOk = IsImpulse(c2, atr, bullish ? TradeDirection.Bullish : TradeDirection.Bearish);
                if (impulseOk) score += 2;

                bool pullbackOk = IsControlledPullback(c1, atr, ema9);
                if (pullbackOk) score += 2;

                bool continuationOk = IsContinuation(c0, atr, bullish ? TradeDirection.Bullish : TradeDirection.Bearish);
                if (continuationOk) score += 2;

                bool brokeStructure = BreaksStructure(ratesH1, c0, atr, bullish ? TradeDirection.Bullish : TradeDirection.Bearish);
                if (brokeStructure) score += 1;

                // =========================
                // MOMENTUM (RELAXED)
                // =========================
                bool strongMomentum = MomentumEvaluator.IsStrong(c0, atr, 0.6, 0.45);

                if (!strongMomentum && !continuationOk)
                {
                    ev.Regime = "WeakMomentum";
                    return TradeAction.Hold;
                }

                // =========================
                // M15 CONFIRMATION
                // =========================
                TradeAction direction =
                    bullish ? TradeAction.Bullish : TradeAction.Bearish;

                string reason = "";
                if (M15Service.IsTradeAllowed(symbol, direction, EntryModel.Sniper, ctx.CurrentMomentum, out reason))
                    score += 1;

                // =========================
                // FINAL SCORE
                // =========================
                if (score < MinScore)
                {
                    ev.Regime = "LowScore";
                    ev.ActionDescription = $"Score too low ({score})";
                    return TradeAction.Hold;
                }

                var action = bullish ? TradeAction.EnterBuy : TradeAction.EnterSell;

                LogFile.Write($"[SNIPER V4] {symbol} - Score: {score} - Action: {action}");

                ev.Regime = "ContinuationEntry";
                ev.ActionDescription = $"Sniper score = {score}";
                ev.SniperAction = action.ToString();

                return action;
            }
            catch
            {
                return TradeAction.Hold;
            }
        }

        // =========================
        // HELPERS
        // =========================

        private static bool IsImpulse(MqlRates c, double atr, TradeDirection dir)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double ratio = body / range;

            bool dirOk = dir == TradeDirection.Bullish ? c.close > c.open : c.close < c.open;

            return dirOk && range >= atr * ImpulseRangeAtr && ratio >= ImpulseBodyRatio;
        }

        private static bool IsControlledPullback(MqlRates c, double atr, double ema9)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double wickRatio = (range - body) / range;

            double mid = (c.open + c.close) / 2;

            return range <= atr * PullbackMaxAtr &&
                   wickRatio <= PullbackWickRatioMax &&
                   Math.Abs(mid - ema9) <= atr * PullbackNearEmaAtr;
        }

        private static bool IsContinuation(MqlRates c, double atr, TradeDirection dir)
        {
            double range = c.high - c.low;
            if (range <= 0) return false;

            double body = Math.Abs(c.close - c.open);
            double ratio = body / range;

            bool dirOk = dir == TradeDirection.Bullish ? c.close > c.open : c.close < c.open;

            return dirOk &&
                   range >= atr * ContinuationRangeAtr &&
                   ratio >= ContinuationBodyRatio;
        }

        private static bool BreaksStructure(MqlRates[] rates, MqlRates c0, double atr, TradeDirection dir)
        {
            double hh = double.MinValue;
            double ll = double.MaxValue;

            for (int i = 2; i < 2 + StructureLookback; i++)
            {
                var c = rates[^i];
                hh = Math.Max(hh, c.high);
                ll = Math.Min(ll, c.low);
            }

            double buffer = atr * 0.05;

            return dir == TradeDirection.Bullish
                ? c0.high > hh - buffer
                : c0.low < ll + buffer;
        }
    }
}
