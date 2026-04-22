using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Util.Trading.Execution;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Services
{
    public static class BreakoutContinuationEntryEngine
    {
        public static double MinAtrPercentile = 0.70;
        public static double BodyAtrMin = 0.55;
        public static double CloseNearExtreme = 0.75;
        public static int StructureLookback = 8;
        public static int CooldownMinutes = 60;

        public static double MaxSpreadAtrFactor = 0.25;

        private static readonly Dictionary<string, DateTime> _lastEntryUtc = new();
        private static int breakoutCounter = 0;

        public static void breakoutContinuationCounter()
        {
            LogFile.Write($"Break test entries count: {breakoutCounter + 1}");
            breakoutCounter++;
        }

        public static TradeAction Decide(
            string symbol,
            MtApi5Client client,
            MqlRates[] ratesH1Closed,
            double atr,
            double atrPercentile,
            double[] emaFastH1,
            double[] emaSlowH1,
            SymbolContext ctx,
            M15ConfirmationService? m15Service,
            out SniperEvent ev)
        {
            ev = new SniperEvent
            {
                Regime = "BreakoutContinuation",
                ActionDescription = "No setup",
                SniperAction = TradeAction.Hold.ToString()
            };

            if (ratesH1Closed == null || ratesH1Closed.Length < StructureLookback + 5)
                return TradeAction.Hold;

            if (emaFastH1 == null || emaSlowH1 == null)
                return TradeAction.Hold;

            if (atr <= 0)
                return TradeAction.Hold;

            var lastClosed = ratesH1Closed[^1];
            var prevClosed = ratesH1Closed[^2];

            double ema9 = emaFastH1[^1];
            double ema21 = emaSlowH1[^1];

            // --------------------------------
            // IMPULSE COOLDOWN GUARD
            // --------------------------------
            double impulseRange = lastClosed.high - lastClosed.low;

            if (impulseRange > atr * 1.8)
            {
                ev.Regime = "ImpulseBlock";
                ev.ActionDescription = "Large impulse candle";
                return TradeAction.Hold;
            }

            // --------------------------------
            // EXTENSION GUARD
            // --------------------------------
            double distanceFromEMA9 = Math.Abs(lastClosed.close - ema9);

            if (distanceFromEMA9 > atr * 1.2)
            {
                ev.Regime = "Extended";
                ev.ActionDescription = "Price too far from EMA9";
                return TradeAction.Hold;
            }

            // --------------------------------
            // TREND POSITION GUARD
            // --------------------------------
            double distanceFromEMA21 = Math.Abs(lastClosed.close - ema21);

            if (distanceFromEMA21 > atr * 2.5)
            {
                ev.Regime = "TrendExhausted";
                ev.ActionDescription = "Trend too extended";
                return TradeAction.Hold;
            }

            // --------------------------------
            // EMA SLOPE FILTER
            // --------------------------------
            double slopeNow = emaFastH1[^1] - emaFastH1[^2];
            double slopePrev = emaFastH1[^2] - emaFastH1[^3];

            if (Math.Abs(slopeNow) < Math.Abs(slopePrev) * 0.5)
            {
                ev.Regime = "WeakSlope";
                ev.ActionDescription = "EMA momentum weakening";
                return TradeAction.Hold;
            }

            // --------------------------------
            // ARMED STATE
            // --------------------------------
            if (ctx.State != MarketState.Armed)
            {
                ev.Regime = "NotArmed";
                return TradeAction.Hold;
            }

            // --------------------------------
            // ATR REGIME
            // --------------------------------
            if (atrPercentile < MinAtrPercentile)
            {
                ev.Regime = "LowATR";
                return TradeAction.Hold;
            }

            // --------------------------------
            // SPREAD GUARD
            // --------------------------------
            if (ExecutionGuards.IsSpreadTooWide(client, symbol, atr, MaxSpreadAtrFactor))
            {
                ev.Regime = "SpreadBlock";
                return TradeAction.Hold;
            }

            // --------------------------------
            // COOLDOWN
            // --------------------------------
            if (_lastEntryUtc.TryGetValue(symbol, out var lastEntry))
            {
                if ((DateTime.UtcNow - lastEntry).TotalMinutes < CooldownMinutes)
                {
                    ev.Regime = "Cooldown";
                    return TradeAction.Hold;
                }
            }

            // --------------------------------
            // TREND ALIGNMENT
            // --------------------------------
            bool bullish = emaFastH1[^1] > emaSlowH1[^1] && emaFastH1[^1] >= emaFastH1[^2];
            bool bearish = emaFastH1[^1] < emaSlowH1[^1] && emaFastH1[^1] <= emaFastH1[^2];

            if (!bullish && !bearish)
            {
                ev.Regime = "NoTrend";
                return TradeAction.Hold;
            }

            // --------------------------------
            // BREAKOUT CANDLE QUALITY
            // --------------------------------
            double range = lastClosed.high - lastClosed.low;
            if (range <= 0) return TradeAction.Hold;

            double body = Math.Abs(lastClosed.close - lastClosed.open);
            double bodyAtr = body / atr;

            double closePos = (lastClosed.close - lastClosed.low) / range;

            bool closeStrongBull = closePos >= CloseNearExtreme;
            bool closeStrongBear = closePos <= (1.0 - CloseNearExtreme);

            if (bodyAtr < BodyAtrMin)
            {
                ev.Regime = "WeakBody";
                return TradeAction.Hold;
            }

            // --------------------------------
            // STRUCTURE BREAK
            // --------------------------------
            var lookback = ratesH1Closed
                .Skip(Math.Max(0, ratesH1Closed.Length - (StructureLookback + 1)))
                .Take(StructureLookback)
                .ToArray();

            double priorHigh = lookback.Max(c => c.high);
            double priorLow = lookback.Min(c => c.low);

            double breakoutBuffer = atr * 0.10;

            bool brokeUp = lastClosed.close > priorHigh + breakoutBuffer;
            bool brokeDown = lastClosed.close < priorLow - breakoutBuffer;

            TradeAction action = TradeAction.Hold;

            if (bullish && brokeUp && lastClosed.close > lastClosed.open && closeStrongBull)
                action = TradeAction.EnterBuy;

            if (bearish && brokeDown && lastClosed.close < lastClosed.open && closeStrongBear)
                action = TradeAction.EnterSell;

            if (action == TradeAction.Hold)
            {
                ev.Regime = "NoBreakout";
                return TradeAction.Hold;
            }

            // --------------------------------
            // OPTIONAL M15 CONFIRMATION
            // --------------------------------
            if (m15Service != null)
            {
                var dir = action == TradeAction.EnterBuy
                    ? TradeAction.Bullish
                    : TradeAction.Bearish;
                var reason = "";
                if (!m15Service.IsTradeAllowed(symbol, dir, EntryModel.Continuation, ctx.CurrentMomentum, out reason))
                {
                    ev.Regime = "M15Block";
                    return TradeAction.Hold;
                }
            }

            _lastEntryUtc[symbol] = DateTime.UtcNow;

            breakoutContinuationCounter();

            ev.Regime = "BreakoutContinuation";
            ev.ActionDescription = $"Breakout continuation ({action}) | bodyATR={bodyAtr:F2}";
            ev.SniperAction = action.ToString();

            return action;
        }
    }

}
