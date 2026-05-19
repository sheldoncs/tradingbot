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
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public static class BreakoutContinuationEntryEngine
    {
        private class Profile
        {
            public double MinAtrPercentile;
            public double BodyAtrMin;
            public double Ema9DistanceAtr;
            public double Ema21DistanceAtr;
            public double BreakoutBufferAtr;
            public double CloseNearExtreme;
            public int StructureLookback;
            public int CooldownMinutes;
        }

        private static Profile GetProfile(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                symbol = "";

            symbol = symbol.ToUpperInvariant();

            // =============================
            // GOLD / METALS
            // =============================
            if (symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER"))
            {
                return new Profile
                {
                    MinAtrPercentile = 0.50,
                    BodyAtrMin = 0.38,
                    Ema9DistanceAtr = 1.55,
                    Ema21DistanceAtr = 2.80,
                    BreakoutBufferAtr = 0.06,
                    CloseNearExtreme = 0.60,
                    StructureLookback = 6,
                    CooldownMinutes = 35
                };
            }

            // =============================
            // INDICES / CFDs
            // =============================
            if (symbol.Contains("US100") ||
                symbol.Contains("NAS") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US30") ||
                symbol.Contains("DJ") ||
                symbol.Contains("SPX") ||
                symbol.Contains("US500") ||
                symbol.Contains("DAX") ||
                symbol.Contains("GER") ||
                symbol.Contains("UK100"))
            {
                return new Profile
                {
                    MinAtrPercentile = 0.50,
                    BodyAtrMin = 0.32,
                    Ema9DistanceAtr = 1.65,
                    Ema21DistanceAtr = 2.90,
                    BreakoutBufferAtr = 0.06,
                    CloseNearExtreme = 0.58,
                    StructureLookback = 6,
                    CooldownMinutes = 35
                };
            }

            // =============================
            // CRYPTO
            // =============================
            if (symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("AVAX") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("DOGE") ||
                symbol.Contains("LTC") ||
                symbol.Contains("LINK"))
            {
                return new Profile
                {
                    MinAtrPercentile = 0.52,
                    BodyAtrMin = 0.34,
                    Ema9DistanceAtr = 1.85,
                    Ema21DistanceAtr = 3.10,
                    BreakoutBufferAtr = 0.07,
                    CloseNearExtreme = 0.58,
                    StructureLookback = 6,
                    CooldownMinutes = 35
                };
            }

            // =============================
            // FX DEFAULT
            // =============================
            return new Profile
            {
                MinAtrPercentile = 0.45,
                BodyAtrMin = 0.28,
                Ema9DistanceAtr = 1.20,
                Ema21DistanceAtr = 2.20,
                BreakoutBufferAtr = 0.05,
                CloseNearExtreme = 0.58,
                StructureLookback = 6,
                CooldownMinutes = 30
            };
        }

        public static int StructureLookback = 8;
        public static int CooldownMinutes = 60;
        public static double MaxSpreadAtrFactor = 0.25;
        public static double CloseNearExtreme = 0.65;

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

            if (string.IsNullOrWhiteSpace(symbol) ||
                client == null ||
                ctx == null ||
                ratesH1Closed == null ||
                emaFastH1 == null ||
                emaSlowH1 == null ||
                atr <= 0)
            {
                ev.Regime = "InvalidInput";
                return TradeAction.Hold;
            }

            var profile = GetProfile(symbol);

            int lookbackCount = Math.Min(profile.StructureLookback, StructureLookback);

            if (ratesH1Closed.Length < lookbackCount + 5 ||
                emaFastH1.Length < 3 ||
                emaSlowH1.Length < 3)
            {
                ev.Regime = "InsufficientData";
                return TradeAction.Hold;
            }

            double normalizedAtrPercentile = atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            var lastClosed = ratesH1Closed[^1];

            double ema9 = emaFastH1[^1];
            double ema21 = emaSlowH1[^1];

            // --------------------------------
            // ARMED STATE
            // --------------------------------
            if (ctx.State != MarketState.Armed)
            {
                ev.Regime = "NotArmed";
                ev.ActionDescription = $"Blocked: state is {ctx.State}, not Armed";
                return TradeAction.Hold;
            }

            // --------------------------------
            // ATR REGIME
            // --------------------------------
            if (normalizedAtrPercentile < profile.MinAtrPercentile)
            {
                ev.Regime = "LowATR";
                ev.ActionDescription =
                    $"Blocked: ATR percentile too low {normalizedAtrPercentile:F2} < {profile.MinAtrPercentile:F2}";
                return TradeAction.Hold;
            }

            // --------------------------------
            // SPREAD GUARD
            // --------------------------------
            if (ExecutionGuards.IsSpreadTooWide(client, symbol, atr, MaxSpreadAtrFactor))
            {
                ev.Regime = "SpreadBlock";
                ev.ActionDescription = "Blocked: spread too wide";
                return TradeAction.Hold;
            }

            // --------------------------------
            // COOLDOWN — SYMBOL ADAPTIVE
            // --------------------------------
            int cooldownMinutes = Math.Min(CooldownMinutes, profile.CooldownMinutes);

            if (_lastEntryUtc.TryGetValue(symbol, out var lastEntry))
            {
                double minutesSinceLastEntry = (DateTime.UtcNow - lastEntry).TotalMinutes;

                if (minutesSinceLastEntry < cooldownMinutes)
                {
                    ev.Regime = "Cooldown";
                    ev.ActionDescription =
                        $"Blocked: cooldown active {minutesSinceLastEntry:F1}/{cooldownMinutes} minutes";
                    return TradeAction.Hold;
                }
            }

            // --------------------------------
            // TREND ALIGNMENT
            // --------------------------------
            double ema9Slope = emaFastH1[^1] - emaFastH1[^2];
            double ema21Slope = emaSlowH1[^1] - emaSlowH1[^2];

            bool bullish =
                emaFastH1[^1] > emaSlowH1[^1] &&
                ema9Slope >= 0;

            bool bearish =
                emaFastH1[^1] < emaSlowH1[^1] &&
                ema9Slope <= 0;

            if (!bullish && !bearish)
            {
                ev.Regime = "NoTrend";
                ev.ActionDescription =
                    $"Blocked: no clean trend | ema9Slope={ema9Slope:F5}, ema21Slope={ema21Slope:F5}";
                return TradeAction.Hold;
            }

            bool strongTrend = Math.Abs(ema9Slope) > atr * 0.04;

            // --------------------------------
            // EXTENSION GUARD — DIRECTIONAL
            // --------------------------------
            double distanceFromEMA9 = Math.Abs(lastClosed.close - ema9);
            double distanceFromEMA21 = Math.Abs(lastClosed.close - ema21);

            // In a strong trend, allow slightly more room.
            double ema9Max = atr * profile.Ema9DistanceAtr;
            double ema21Max = atr * profile.Ema21DistanceAtr;

            if (strongTrend)
            {
                ema9Max *= 1.10;
                ema21Max *= 1.05;
            }

            if (distanceFromEMA9 > ema9Max)
            {
                ev.Regime = "Extended";
                ev.ActionDescription =
                    $"Blocked: too far from EMA9 | dist={distanceFromEMA9:F5}, max={ema9Max:F5}";
                return TradeAction.Hold;
            }

            if (distanceFromEMA21 > ema21Max)
            {
                ev.Regime = "TrendExhausted";
                ev.ActionDescription =
                    $"Blocked: too far from EMA21 | dist={distanceFromEMA21:F5}, max={ema21Max:F5}";
                return TradeAction.Hold;
            }

            // --------------------------------
            // CANDLE QUALITY
            // --------------------------------
            double range = lastClosed.high - lastClosed.low;
            if (range <= 0)
            {
                ev.Regime = "InvalidCandle";
                return TradeAction.Hold;
            }

            double body = Math.Abs(lastClosed.close - lastClosed.open);
            double bodyAtr = body / atr;

            double closePos = (lastClosed.close - lastClosed.low) / range;

            bool closeStrongBull = closePos >= profile.CloseNearExtreme;
            bool closeStrongBear = closePos <= (1.0 - profile.CloseNearExtreme);

            bool bullishCandle =
                lastClosed.close > lastClosed.open &&
                bodyAtr >= profile.BodyAtrMin &&
                closeStrongBull;

            bool bearishCandle =
                lastClosed.close < lastClosed.open &&
                bodyAtr >= profile.BodyAtrMin &&
                closeStrongBear;

            if (bullish && !bullishCandle)
            {
                ev.Regime = "WeakBody";
                ev.ActionDescription =
                    $"Blocked BUY: weak breakout candle | bodyAtr={bodyAtr:F2}, closePos={closePos:F2}";
                return TradeAction.Hold;
            }

            if (bearish && !bearishCandle)
            {
                ev.Regime = "WeakBody";
                ev.ActionDescription =
                    $"Blocked SELL: weak breakout candle | bodyAtr={bodyAtr:F2}, closePos={closePos:F2}";
                return TradeAction.Hold;
            }

            // --------------------------------
            // STRUCTURE BREAK
            // Exclude the breakout candle itself from prior structure.
            // This avoids comparing the candle against its own high/low.
            // --------------------------------
            var priorCandles = ratesH1Closed
                .Take(ratesH1Closed.Length - 1)
                .Skip(Math.Max(0, ratesH1Closed.Length - 1 - lookbackCount))
                .ToArray();

            if (priorCandles.Length < lookbackCount)
            {
                ev.Regime = "InsufficientStructure";
                return TradeAction.Hold;
            }

            double priorHigh = priorCandles.Max(c => c.high);
            double priorLow = priorCandles.Min(c => c.low);

            double breakoutBuffer = atr * profile.BreakoutBufferAtr;

            bool closeBrokeUp = lastClosed.close > priorHigh + breakoutBuffer;
            bool closeBrokeDown = lastClosed.close < priorLow - breakoutBuffer;

            // Allows a wick break if the candle still closes strong in the breakout direction.
            bool wickBrokeUp =
                lastClosed.high > priorHigh + breakoutBuffer &&
                lastClosed.close > priorHigh - atr * 0.03 &&
                closeStrongBull;

            bool wickBrokeDown =
                lastClosed.low < priorLow - breakoutBuffer &&
                lastClosed.close < priorLow + atr * 0.03 &&
                closeStrongBear;

            bool brokeUp = closeBrokeUp || wickBrokeUp;
            bool brokeDown = closeBrokeDown || wickBrokeDown;

            TradeAction action = TradeAction.Hold;

            if (bullish && brokeUp && bullishCandle)
                action = TradeAction.EnterBuy;

            if (bearish && brokeDown && bearishCandle)
                action = TradeAction.EnterSell;

            if (action == TradeAction.Hold)
            {
                ev.Regime = "NoBreakout";
                ev.ActionDescription =
                    $"Blocked: no valid structure break | " +
                    $"priorHigh={priorHigh:F5}, priorLow={priorLow:F5}, " +
                    $"close={lastClosed.close:F5}, high={lastClosed.high:F5}, low={lastClosed.low:F5}, " +
                    $"brokeUp={brokeUp}, brokeDown={brokeDown}";
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

                if (!m15Service.IsTradeAllowed(
                        symbol,
                        dir,
                        EntryModel.Continuation,
                        ctx.CurrentMomentum,
                        out reason))
                {
                    ev.Regime = "M15Block";
                    ev.ActionDescription = $"Blocked by M15: {reason}";
                    return TradeAction.Hold;
                }
            }

            _lastEntryUtc[symbol] = DateTime.UtcNow;

            ev.Regime = "BreakoutContinuation";
            ev.ActionDescription =
                $"Breakout continuation {action} | " +
                $"ATR%={normalizedAtrPercentile:F2}, bodyAtr={bodyAtr:F2}, " +
                $"distEMA9={distanceFromEMA9:F5}, distEMA21={distanceFromEMA21:F5}, " +
                $"priorHigh={priorHigh:F5}, priorLow={priorLow:F5}";

            ev.SniperAction = action.ToString();

            LogFile.Write(
                $"[{symbol}] BreakoutContinuation {action} | " +
                $"ATR%={normalizedAtrPercentile:F2}, bodyAtr={bodyAtr:F2}, closePos={closePos:F2}, " +
                $"priorHigh={priorHigh:F5}, priorLow={priorLow:F5}, " +
                $"distEMA9={distanceFromEMA9:F5}, strongTrend={strongTrend}"
            );

            return action;
        }
    }

}
