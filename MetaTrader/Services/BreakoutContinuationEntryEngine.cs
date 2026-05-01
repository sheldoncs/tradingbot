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
        private class Profile
        {
            public double MinAtrPercentile;
            public double BodyAtrMin;
            public double Ema9DistanceAtr;
            public double BreakoutBufferAtr;
        }

        private static Profile GetProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
            {
                return new Profile
                {
                    MinAtrPercentile = 0.60,
                    BodyAtrMin = 0.45,          // 🔧 relaxed
                    Ema9DistanceAtr = 1.6,
                    BreakoutBufferAtr = 0.10
                };
            }

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
            {
                return new Profile
                {
                    MinAtrPercentile = 0.55,
                    BodyAtrMin = 0.35,          // 🔧 relaxed
                    Ema9DistanceAtr = 1.7,
                    BreakoutBufferAtr = 0.10    // 🔧 slightly reduced
                };
            }

            if (symbol.Contains("USD") &&
                (symbol.Contains("ETH") || symbol.Contains("BTC") ||
                 symbol.Contains("SOL") || symbol.Contains("AVAX") ||
                 symbol.Contains("BNB")))
            {
                return new Profile
                {
                    MinAtrPercentile = 0.65,
                    BodyAtrMin = 0.40,          // 🔧 relaxed
                    Ema9DistanceAtr = 2.0,
                    BreakoutBufferAtr = 0.12    // 🔧 relaxed
                };
            }

            // FX default
            return new Profile
            {
                MinAtrPercentile = 0.50,
                BodyAtrMin = 0.30,              // 🔧 relaxed
                Ema9DistanceAtr = 1.3,
                BreakoutBufferAtr = 0.08
            };
        }

        public static int StructureLookback = 8;
        public static int CooldownMinutes = 60;
        public static double MaxSpreadAtrFactor = 0.25;
        public static double CloseNearExtreme = 0.65; // 🔧 relaxed from 0.75

        private static readonly Dictionary<string, DateTime> _lastEntryUtc = new();

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

            if (emaFastH1 == null || emaSlowH1 == null || atr <= 0)
                return TradeAction.Hold;

            var profile = GetProfile(symbol);

            var lastClosed = ratesH1Closed[^1];

            double ema9 = emaFastH1[^1];
            double ema21 = emaSlowH1[^1];

            // --------------------------------
            // EXTENSION GUARD (keep as-is)
            // --------------------------------
            double distanceFromEMA9 = Math.Abs(lastClosed.close - ema9);

            if (distanceFromEMA9 > atr * profile.Ema9DistanceAtr)
            {
                ev.Regime = "Extended";
                return TradeAction.Hold;
            }

            // --------------------------------
            // TREND POSITION GUARD
            // --------------------------------
            double distanceFromEMA21 = Math.Abs(lastClosed.close - ema21);

            if (distanceFromEMA21 > atr * 2.5)
            {
                ev.Regime = "TrendExhausted";
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
            if (atrPercentile < profile.MinAtrPercentile)
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
            // CANDLE QUALITY (RELAXED)
            // --------------------------------
            double range = lastClosed.high - lastClosed.low;
            if (range <= 0) return TradeAction.Hold;

            double body = Math.Abs(lastClosed.close - lastClosed.open);
            double bodyAtr = body / atr;

            double closePos = (lastClosed.close - lastClosed.low) / range;

            bool closeStrongBull = closePos >= CloseNearExtreme;
            bool closeStrongBear = closePos <= (1.0 - CloseNearExtreme);

            if (bodyAtr < profile.BodyAtrMin)
            {
                ev.Regime = "WeakBody";
                return TradeAction.Hold;
            }

            // --------------------------------
            // STRUCTURE BREAK (RELAXED BUFFER)
            // --------------------------------
            var lookback = ratesH1Closed
                .Skip(Math.Max(0, ratesH1Closed.Length - (StructureLookback + 1)))
                .Take(StructureLookback)
                .ToArray();

            double priorHigh = lookback.Max(c => c.high);
            double priorLow = lookback.Min(c => c.low);

            double breakoutBuffer = atr * profile.BreakoutBufferAtr;

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
            // OPTIONAL M15 CONFIRMATION (unchanged)
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

            ev.Regime = "BreakoutContinuation";
            ev.ActionDescription = $"Breakout continuation ({action})";
            ev.SniperAction = action.ToString();

            return action;
        }
    }

}
