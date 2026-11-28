using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.Helper.MetaTrader.Services.Helper;
using MetaTrader.Services.MetaTrader.Services;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;
using System.Collections.Generic;
using static MetaTrader.Services.HybridMomentumEngine;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Services.Helper
{
    public static class FastEntryEngine
    {
        // GOLD continuation advisor is active as an ADDITIVE entry candidate path.
        // All candidates must pass FastEntry's original follow-through, S/R,
        // M15, wick and live-exhaustion gates before TradeService can execute.
        // It never places orders or changes position risk management.
        private static readonly GoldTrendContinuationAdvisor _goldTrendAdvisor =
            new GoldTrendContinuationAdvisor();
        private static readonly object _goldTrendAdvisorLogSync = new object();
        private static readonly System.Collections.Generic.Dictionary<string, DateTime>
            _goldTrendAdvisorLastLoggedUtc = new System.Collections.Generic.Dictionary<string, DateTime>(
                StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Generic.Dictionary<string, string>
            _goldTrendAdvisorLastState = new System.Collections.Generic.Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        // GOLD: do not accept a transient EMA lean or a single large candle
        // as an escape from a sideways market. Track real range breaks separately
        // for XAUUSD and XAUEUR; observations reset when the setup weakens.
        private sealed class GoldRangeEscapeCandidate
        {
            public int Direction; // +1 BUY, -1 SELL
            public double Boundary;
            public DateTime FirstSeenUtc;
        }

        private static readonly object _goldRangeEscapeSync = new object();
        private static readonly Dictionary<string, GoldRangeEscapeCandidate>
            _goldRangeEscapeBySymbol =
                new Dictionary<string, GoldRangeEscapeCandidate>(StringComparer.OrdinalIgnoreCase);

        // Separate persistence for emerging trends; never inherit a range-escape timer.
        private sealed class GoldEmergingTrendCandidate
        {
            public int Direction;
            public string BarTime;
            public DateTime FirstSeenUtc;
            public DateTime LastSeenUtc;
        }

        private static readonly Dictionary<string, GoldEmergingTrendCandidate>
            _goldEmergingTrendBySymbol =
                new Dictionary<string, GoldEmergingTrendCandidate>(StringComparer.OrdinalIgnoreCase);



        // Established trend classification only: six completed H1 bars provide
        // historical confirmation independently of current impulse strength.
        // Candle colour is deliberately not used. This does not approve orders.
        internal static int GetEstablishedChopTrendDirection(
            MqlRates[] closedRates, double[] closedEmaFast, double[] closedEmaSlow,
            double atr, double livePrice)
        {
            if (closedRates == null || closedRates.Length < 6 ||
                closedEmaFast == null || closedEmaFast.Length < 3 ||
                closedEmaSlow == null || closedEmaSlow.Length < 3 ||
                (double.IsNaN(atr) || double.IsInfinity(atr)) || atr <= 0 ||
                (double.IsNaN(livePrice) || double.IsInfinity(livePrice)) || livePrice <= 0)
                return 0;

            double e9 = closedEmaFast[^1], e21 = closedEmaSlow[^1];
            if (double.IsNaN(e9) || double.IsInfinity(e9) || e9 <= 0 ||
                double.IsNaN(e21) || double.IsInfinity(e21) || e21 <= 0)
                return 0;
            double l9 = e9 + 0.20 * (livePrice - e9);
            double l21 = e21 + (2.0 / 22.0) * (livePrice - e21);
            if (Math.Abs(e9 - e21) / atr < 0.40 || Math.Abs(l9 - l21) / atr < 0.30)
                return 0;

            int side = e9 > e21 ? 1 : -1;
            // Both closed EMAs must progress in the same direction over two
            // completed intervals, and live price must maintain that alignment.
            for (int i = 1; i <= 2; i++)
            {
                int j = closedEmaFast.Length - i;
                int k = closedEmaSlow.Length - i;
                if (double.IsInfinity(closedEmaFast[j - 1]) || double.IsInfinity(closedEmaSlow[k - 1]))
                    return 0;
                if (!(side * (closedEmaFast[j] - closedEmaFast[j - 1]) > 0 &&
                      side * (closedEmaSlow[k] - closedEmaSlow[k - 1]) > 0))
                    return 0;
            }
            if (!(side * (l9 - l21) > 0 && side * (livePrice - l9) > 0 &&
                  side * (livePrice - l21) > 0 && side * (l9 - e9) > 0 &&
                  side * (l21 - e21) > 0))
                return 0;

            int start = closedRates.Length - 6;
            double earlyHigh = 0, earlyLow = 0, lateHigh = 0, lateLow = 0;
            int advancingHighs = 0, advancingLows = 0;
            for (int i = start; i < closedRates.Length; i++)
            {
                var bar = closedRates[i];
                if ((double.IsNaN(bar.high) || double.IsInfinity(bar.high)) || (double.IsNaN(bar.low) || double.IsInfinity(bar.low)) ||
                    bar.low <= 0 || bar.high < bar.low ||
                    (i > start && bar.time <= closedRates[i - 1].time))
                    return 0;
                if (i < start + 3) { earlyHigh += bar.high; earlyLow += bar.low; }
                else { lateHigh += bar.high; lateLow += bar.low; }
                if (i > start)
                {
                    if (side * (bar.high - closedRates[i - 1].high) > 0) advancingHighs++;
                    if (side * (bar.low - closedRates[i - 1].low) > 0) advancingLows++;
                }
            }
            // Allow normal pullbacks, but require both the highs and lows of
            // the recent half to shift directionally by at least 0.15 ATR.
            if (advancingHighs < 2 || advancingLows < 2 ||
                !(side * (lateHigh - earlyHigh) / 3.0 >= 0.15 * atr &&
                  side * (lateLow - earlyLow) / 3.0 >= 0.15 * atr &&
                  side * (closedRates[^1].close - closedRates[start].close) >= 0.25 * atr))
                return 0;
            return side;
        }

        private static bool ShouldAvoidChoppyMarket(
            string symbol, string marketState,
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow,
            double atr,
            MqlRates liveCandle,
            double livePrice,
            double momentum,
            out int emergingDirection)
        {
            emergingDirection = 0;
            // Called before FastEntryEngine: includes impulses, structural
            // break candidates, and all other entries from this service.
            if (string.IsNullOrWhiteSpace(symbol) ||
                rates == null || rates.Length < 8 ||
                emaFast == null || emaFast.Length < 2 ||
                emaSlow == null || emaSlow.Length < 2 ||
                atr <= 0 || double.IsNaN(atr) || double.IsInfinity(atr) ||
                livePrice <= 0 || double.IsNaN(livePrice) || double.IsInfinity(livePrice) ||
                liveCandle.open <= 0 || double.IsNaN(liveCandle.open))
            {
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    lock (_goldRangeEscapeSync)
                        _goldEmergingTrendBySymbol.Remove(symbol);
                }
                LogFile.Write($"[{symbol ?? "UNKNOWN"}] GOLD CHOP DATA INVALID | decision=BLOCK");
                return true;
            }


            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];
            double priorEma9 = emaFast[^2];
            double spreadAtr = Math.Abs(ema9 - ema21) / atr;
            double liveBodyAtr = Math.Abs(livePrice - liveCandle.open) / atr;

            // The last six COMPLETED H1 candles define a fixed historical
            // range. Never add the live candle to the range: doing so would
            // move the breakout boundary as price moves.
            const int RangeBars = 6;
            double rangeHigh = double.MinValue;
            double rangeLow = double.MaxValue;
            int directionChanges = 0;
            bool? previousDirection = null;
            for (int i = rates.Length - RangeBars; i < rates.Length; i++)
            {
                MqlRates bar = rates[i];
                rangeHigh = Math.Max(rangeHigh, bar.high);
                rangeLow = Math.Min(rangeLow, bar.low);
                if (bar.close == bar.open) continue; // doji is neutral
                bool bullish = bar.close > bar.open;
                if (previousDirection.HasValue && previousDirection.Value != bullish)
                    directionChanges++;
                previousDirection = bullish;
            }

            double rangeAtr = (rangeHigh - rangeLow) / atr;
            bool compressed = spreadAtr < 0.30;
            bool whipsaw = directionChanges >= 3;
            bool tightRange = rangeAtr <= 2.50;
            bool compressionState = string.Equals(marketState, "Compression", StringComparison.OrdinalIgnoreCase);
            bool sideways = compressionState ||
                (compressed && (directionChanges >= 2 || tightRange)) ||
                (whipsaw && tightRange);

            // Do not carry a stale breakout timer into the next sideways zone.
            // Established trend is a market classification, not a fresh impulse.
            int establishedDirection = sideways && IsGold(symbol.ToUpperInvariant()) ? GetEstablishedChopTrendDirection(
                rates, emaFast, emaSlow, atr, livePrice) : 0;
            if (establishedDirection != 0)
            {
                lock (_goldRangeEscapeSync)
                {
                    _goldRangeEscapeBySymbol.Remove(symbol);
                    _goldEmergingTrendBySymbol.Remove(symbol);
                }
                emergingDirection = establishedDirection;
                LogFile.Write($"[{symbol}] GOLD RANGE/CHOP GATE | decision=ALLOW_ESTABLISHED_TREND | " +
                    $"direction={(establishedDirection > 0 ? "BUY" : "SELL")} | spreadATR={spreadAtr:F2} | " +
                    $"flips={directionChanges} | rangeATR={rangeAtr:F2} | momentum={momentum:F2}");
                return false;
            }

            if (!sideways)
            {
                lock (_goldRangeEscapeSync)
                {
                    _goldRangeEscapeBySymbol.Remove(symbol);
                    _goldEmergingTrendBySymbol.Remove(symbol);
                }
                return false;
            }

            // While in chop, require direction, a real boundary clearance,
            // meaningful live-body expansion and persistence. M15, S/R and
            // the existing impulse protections are still separate vetoes.
            const double MinOutsideRangeAtr = 0.12;
            const double MinEscapeBodyAtr = 0.70;
            const double ConfirmSeconds = 15.0;

            // Calculate live H1 EMA estimates from the LAST CLOSED H1 EMAs.
            // A fresh breakout can establish a live trend before the closed
            // EMA9/EMA21 alignment catches up. Keep the old closed-EMA route,
            // but allow a second, stronger live-EMA route with momentum proof.
            // Neither route bypasses the existing 0.12 ATR range clearance,
            // 0.70 ATR directional body, or 15-second persistence check.
            double liveEma9 = ema9 + 0.20 * (livePrice - ema9);
            double liveEma21 = ema21 + (2.0 / 22.0) * (livePrice - ema21);
            bool validEscapeMomentum =
                !double.IsNaN(momentum) && !double.IsInfinity(momentum) &&
                momentum >= 0.60;

            bool closedBullAlignment = ema9 > ema21 && ema9 > priorEma9;
            bool closedBearAlignment = ema9 < ema21 && ema9 < priorEma9;
            bool liveBullAlignment = validEscapeMomentum &&
                liveEma9 > liveEma21 && liveEma9 > ema9 &&
                liveEma21 > ema21 &&
                livePrice > liveEma9 && livePrice > liveEma21;
            bool liveBearAlignment = validEscapeMomentum &&
                liveEma9 < liveEma21 && liveEma9 < ema9 &&
                liveEma21 < ema21 &&
                livePrice < liveEma9 && livePrice < liveEma21;

            bool bullishEscape =
                livePrice >= rangeHigh + MinOutsideRangeAtr * atr &&
                livePrice > liveCandle.open &&
                liveBodyAtr >= MinEscapeBodyAtr &&
                ((closedBullAlignment && livePrice > ema9 && livePrice > ema21) ||
                  liveBullAlignment);

            bool bearishEscape =
                livePrice <= rangeLow - MinOutsideRangeAtr * atr &&
                livePrice < liveCandle.open &&
                liveBodyAtr >= MinEscapeBodyAtr &&
                ((closedBearAlignment && livePrice < ema9 && livePrice < ema21) ||
                  liveBearAlignment);

            int direction = bullishEscape ? 1 : bearishEscape ? -1 : 0;
            double boundary = direction > 0 ? rangeHigh : rangeLow;
            bool escapeConfirmed = false;
            DateTime now = DateTime.UtcNow;

            lock (_goldRangeEscapeSync)
            {
                if (direction == 0)
                {
                    _goldRangeEscapeBySymbol.Remove(symbol);
                }
                else if (_goldRangeEscapeBySymbol.TryGetValue(symbol, out var existing) &&
                         existing.Direction == direction &&
                         Math.Abs(existing.Boundary - boundary) <= 0.01 * atr)
                {
                    escapeConfirmed = (now - existing.FirstSeenUtc).TotalSeconds >= ConfirmSeconds;
                }
                else
                {
                    _goldRangeEscapeBySymbol[symbol] = new GoldRangeEscapeCandidate
                    {
                        Direction = direction,
                        Boundary = boundary,
                        FirstSeenUtc = now
                    };
                }
            }

            // GOLD ONLY: a separated, directional market can emerge from historical
            // chop without first breaking the entire six-H1 range. Use live EMA
            // estimates consistent with FastEntry; retain the original escape path.
            bool emergingBase =
                (symbol.StartsWith("XAU", StringComparison.OrdinalIgnoreCase) ||
                 symbol.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase)) &&
                !compressed && spreadAtr >= 0.40 && directionChanges <= 3 &&
                !double.IsNaN(momentum) && !double.IsInfinity(momentum) &&
                momentum >= 0.60 && liveBodyAtr >= 0.65;
            bool emergingBuy = emergingBase && livePrice > liveCandle.open &&
                liveEma9 > ema9 && livePrice > liveEma9 && livePrice > liveEma21;
            bool emergingSell = emergingBase && livePrice < liveCandle.open &&
                liveEma9 < ema9 && livePrice < liveEma9 && livePrice < liveEma21;
            int candidateDirection = emergingBuy ? 1 : emergingSell ? -1 : 0;
            bool emergingConfirmed = false;
            double emergingSeconds = 0;
            string barTime = rates[^1].time.ToString();
            lock (_goldRangeEscapeSync)
            {
                if (candidateDirection == 0 || escapeConfirmed)
                    _goldEmergingTrendBySymbol.Remove(symbol);
                else
                {
                    if (!_goldEmergingTrendBySymbol.TryGetValue(symbol, out var watch) ||
                        watch.Direction != candidateDirection || watch.BarTime != barTime ||
                        now < watch.LastSeenUtc || (now - watch.LastSeenUtc).TotalSeconds > 30)
                    {
                        watch = new GoldEmergingTrendCandidate
                        {
                            Direction = candidateDirection,
                            BarTime = barTime,
                            FirstSeenUtc = now,
                            LastSeenUtc = now
                        };
                        _goldEmergingTrendBySymbol[symbol] = watch;
                    }
                    else
                    {
                        watch.LastSeenUtc = now;
                        emergingSeconds = (now - watch.FirstSeenUtc).TotalSeconds;
                        emergingConfirmed = emergingSeconds >= 10.0;
                    }
                }
            }
            if (emergingConfirmed)
                emergingDirection = candidateDirection;

            LogFile.Write($"[{symbol}] GOLD RANGE/CHOP GATE | " +
                $"decision={(escapeConfirmed ? "ALLOW_CONFIRMED_RANGE_ESCAPE" : emergingConfirmed ? "ALLOW_EMERGING_TREND" : "BLOCK_CHOP_OR_UNCONFIRMED_ESCAPE")} | " +
                $"state={marketState} | compressed={compressed} | spreadATR={spreadAtr:F2} | " +
                $"flips={directionChanges} | rangeATR={rangeAtr:F2} | " +
                $"rangeHigh={rangeHigh:F5} | rangeLow={rangeLow:F5} | " +
                $"price={livePrice:F5} | bodyATR={liveBodyAtr:F2} | " +
                $"bullEscape={bullishEscape} | bearEscape={bearishEscape} | " +
                $"liveBullAlignment={liveBullAlignment} | liveBearAlignment={liveBearAlignment} | " +
                $"closedBullAlignment={closedBullAlignment} | closedBearAlignment={closedBearAlignment} | " +
                $"persistent15s={escapeConfirmed} | momentum={momentum:F2} | " +
                $"liveEMA9={liveEma9:F5} | liveEMA21={liveEma21:F5} | " +
                $"emergingBuy={emergingBuy} | emergingSell={emergingSell} | " +
                $"emergingSeconds={emergingSeconds:F1}/10");

            return !(escapeConfirmed || emergingConfirmed);
        }
        // GOLD FINAL MOMENTUM-EXHAUSTION GUARD (XAUUSD / XAUEUR).
        // Target a narrowly defined weak, sharply fading signal at a recent
        // structural boundary. Do not reject trend entries merely because
        // momentum is lower than its prior reading.
        // Runs after FastEntry's existing impulse/M15/SR/wick approvals, but
        // before any broker-order attempt. No effect on live position exits.
        private static bool RejectGoldWeakBoundaryEntry(
            string symbol, TradeAction action, MqlRates[] closedH1,
            double atr, double liveOpen, double bid, double ask,
            double momentum, double previousMomentum, EntryModel model)
        {
            if (string.IsNullOrWhiteSpace(symbol) ||
                !(symbol.StartsWith("XAU", StringComparison.OrdinalIgnoreCase) ||
                  symbol.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase)) ||
                (action != TradeAction.EnterBuy && action != TradeAction.EnterSell) ||
                closedH1 == null || closedH1.Length < 6 ||
                atr <= 0 || double.IsNaN(atr) || double.IsInfinity(atr) ||
                bid <= 0 || ask < bid || liveOpen <= 0 ||
                double.IsNaN(momentum) || double.IsInfinity(momentum) ||
                double.IsNaN(previousMomentum) || double.IsInfinity(previousMomentum))
                return false; // Existing entry gates remain responsible for invalid data.

            bool buy = action == TradeAction.EnterBuy;
            double mid = (bid + ask) * 0.5;
            double directionalBodyAtr = (buy ? mid - liveOpen : liveOpen - mid) / atr;
            double boundary = buy
                ? closedH1.Skip(closedH1.Length - 6).Max(r => r.high)
                : closedH1.Skip(closedH1.Length - 6).Min(r => r.low);
            double executablePrice = buy ? ask : bid;
            double boundaryDistanceAtr = Math.Abs(executablePrice - boundary) / atr;

            // Require all warning signs together: a large decline in the
            // existing nonnegative hybrid-strength metric, weak present
            // strength, insufficient directional body, and price near the
            // six-completed-H1 structural edge. A strong breakout is not vetoed.
            bool severeFade = previousMomentum >= 0.60 &&
                              momentum >= 0.0 &&
                              momentum <= previousMomentum * 0.45;
            bool weakNow = momentum <= 0.55;
            bool weakDirectionalBody = directionalBodyAtr < 0.65;
            bool nearBoundary = boundaryDistanceAtr <= 0.30;
            bool reject = severeFade && weakNow && weakDirectionalBody && nearBoundary;

            if (reject)
                LogFile.Write($"[{symbol}] GOLD FINAL MOMENTUM EXHAUSTION BLOCK | " +
                    $"action={action} | model={model} | momentum={momentum:F3} | " +
                    $"previousMomentum={previousMomentum:F3} | " +
                    $"bodyATR={directionalBodyAtr:F3} | " +
                    $"boundary={boundary:F5} | boundaryDistanceATR={boundaryDistanceAtr:F3} | " +
                    "reason=SEVERE_FADE_WEAK_BODY_NEAR_H1_BOUNDARY");
            return reject;
        }
        private static TradeAction EvaluateGoldTrendContinuation(
            string symbol, MqlRates lastClosedH1, MqlRates currentH1,
            double atr, double bid, double ask,
            double liveEma9, double closedEma9,
            double liveEma21, double closedEma21,
            double recentHigh, double recentLow,
            M15ConfirmationService m15Service)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !IsGold(symbol))
                return TradeAction.Hold;
            TradeAction candidate = TradeAction.Hold;
            try
            {
                var m15 = m15Service == null ? null : m15Service.GetSnapshot(symbol);
                foreach (GoldTrendContinuationAdvisor.Direction side in
                    new[] { GoldTrendContinuationAdvisor.Direction.Buy,
                            GoldTrendContinuationAdvisor.Direction.Sell })
                {
                    bool isBuy = side == GoldTrendContinuationAdvisor.Direction.Buy;
                    var assessment = _goldTrendAdvisor.Evaluate(
                        new GoldTrendContinuationAdvisor.Snapshot
                        {
                            Symbol = symbol,
                            Side = side,
                            TimeUtc = DateTime.UtcNow,
                            Bid = bid,
                            Ask = ask,
                            Atr = atr,
                            Ema9 = liveEma9,
                            PreviousEma9 = closedEma9,
                            Ema21 = liveEma21,
                            PreviousEma21 = closedEma21,
                            CurrentH1Open = currentH1.open,
                            RecentClosedHigh = recentHigh,
                            RecentClosedLow = recentLow,
                            LastClosedOpen = lastClosedH1.open,
                            LastClosedClose = lastClosedH1.close,
                            HasM15Data = m15 != null,
                            M15Aligned = m15 != null && (isBuy ? m15.Bullish : m15.Bearish),
                            // These values are not available from FastEntry. Never
                            // pretend missing broker risk economics are known.
                            HardLossUsd = 0,
                            TickSize = 0,
                            TickValueInAccountCurrencyPerLot = 0,
                            Lots = 0,
                            HasOpposingSrData = false,
                            PositionOpen = false
                        });

                    if (!assessment.Valid)
                    {
                        LogFile.Write($"[{symbol}] GOLD ADVISOR INVALID | side={side} | reason={assessment.Reason}");
                        continue;
                    }
                    string key = symbol + "|" + side;
                    string state = assessment.Location + "|" + assessment.Health + "|" +
                        assessment.Continuation + "|" + assessment.WouldConsiderFreshContinuation;
                    bool shouldLog;
                    lock (_goldTrendAdvisorLogSync)
                    {
                        DateTime last;
                        string previous;
                        shouldLog = !_goldTrendAdvisorLastLoggedUtc.TryGetValue(key, out last) ||
                            DateTime.UtcNow - last >= TimeSpan.FromSeconds(30) ||
                            !_goldTrendAdvisorLastState.TryGetValue(key, out previous) ||
                            !string.Equals(state, previous, StringComparison.Ordinal);
                        if (shouldLog)
                        {
                            _goldTrendAdvisorLastLoggedUtc[key] = DateTime.UtcNow;
                            _goldTrendAdvisorLastState[key] = state;
                        }
                    }
                    if (assessment.WouldConsiderFreshContinuation)
                    {
                        // Never allow two opposing active candidates in one tick.
                        var proposed = isBuy ? TradeAction.EnterBuy : TradeAction.EnterSell;
                        if (candidate != TradeAction.Hold && candidate != proposed)
                            return TradeAction.Hold;
                        candidate = proposed;
                    }
                    if (shouldLog || assessment.WouldConsiderFreshContinuation)
                        LogFile.Write($"[{symbol}] GOLD TREND ADVISOR | side={side} | " +
                            $"location={assessment.Location} | health={assessment.Health} | " +
                            $"continuation={assessment.Continuation} | " +
                            $"ema9DistanceATR={assessment.Ema9DistanceAtr:F2} | " +
                            $"h1DirectionalBodyATR={assessment.DirectionalH1BodyAtr:F2} | " +
                            $"breakATR={assessment.BreakoutAtr:F2} | " +
                            $"normalPullbackPossible={assessment.NormalPullbackPossible} | " +
                            $"freshContinuationCandidate={assessment.WouldConsiderFreshContinuation} | " +
                            "mode=LIVE_CANDIDATE | riskRoom=UNKNOWN_NO_BROKER_TICK_ECONOMICS");
                }
            }
            catch (Exception ex)
            {
                // Fail closed on a damaged advisor snapshot: don't invent entries.
                try { LogFile.Write($"[{symbol}] GOLD TREND ADVISOR ERROR | {ex.GetType().Name}: {ex.Message}"); }
                catch { }
                return TradeAction.Hold;
            }
            return candidate;
        }

        // GOLD only: a final compound location + deteriorating momentum veto.
        // The live impulse detector receives current momentum only, so this check
        // belongs at FastEntry where BOTH current and previous values exist.
        // Do not turn a normal deceleration into a blanket rejection: require
        // a substantially extended entry and a mature current H1 directional body.
        // Same logic applies to BUY/SELL, impulse/swing/armed/continuation.
        public static bool RejectGoldExtendedFadingEntry(
            string symbol, TradeAction action, double atr,
            double bid, double ask, double liveOpen, double liveEma9,
            double recentHigh, double recentLow,
            double momentum, double previousMomentum,
            string entryPath)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !IsGold(symbol) ||
                (action != TradeAction.EnterBuy && action != TradeAction.EnterSell) ||
                !FiniteGoldValue(atr) || atr <= 0 ||
                !FiniteGoldValue(bid) || !FiniteGoldValue(ask) || bid <= 0 || ask < bid ||
                !FiniteGoldValue(liveOpen) || liveOpen <= 0 ||
                !FiniteGoldValue(liveEma9) || liveEma9 <= 0 ||
                !FiniteGoldValue(recentHigh) || !FiniteGoldValue(recentLow) ||
                recentHigh <= 0 || recentLow <= 0 ||
                !FiniteGoldValue(momentum) || !FiniteGoldValue(previousMomentum) ||
                previousMomentum <= 0 || momentum < 0)
                return false; // Input validation is owned by the existing entry pipeline.

            bool buy = action == TradeAction.EnterBuy;
            double mid = (bid + ask) * 0.5;
            double entryPrice = buy ? ask : bid;
            double emaDistAtr = Math.Abs(entryPrice - liveEma9) / atr;
            double directionalBodyAtr = Math.Max(0.0,
                buy ? mid - liveOpen : liveOpen - mid) / atr;
            double breakAtr = buy ? (ask - recentHigh) / atr : (recentLow - bid) / atr;
            double retention = momentum / previousMomentum;

            // Pattern A: modest (>=5%) deterioration but only after a clearly
            // expanded breakout, as seen in the losing XAUUSD entry.
            bool fadingExtendedBreakout =
                retention <= 0.95 && momentum <= 0.65 &&
                emaDistAtr >= 1.10 && directionalBodyAtr >= 0.80 &&
                breakAtr >= 0.35;

            // Pattern B: more pronounced (>=15%) deterioration at a similarly
            // mature location, as seen in the losing XAUEUR entry.
            bool sharplyFadingExtendedBreakout =
                retention <= 0.85 && momentum <= 0.65 &&
                emaDistAtr >= 1.00 && directionalBodyAtr >= 0.75 &&
                breakAtr >= 0.25;

            bool reject = fadingExtendedBreakout || sharplyFadingExtendedBreakout;
            if (reject)
                LogFile.Write($"[{symbol}] GOLD EXTENDED FADING MOMENTUM BLOCK | " +
                    $"path={entryPath} | action={action} | momentum={momentum:F3} | " +
                    $"previousMomentum={previousMomentum:F3} | retained={retention:F3} | " +
                    $"emaDistATR={emaDistAtr:F3} | bodyATR={directionalBodyAtr:F3} | " +
                    $"breakATR={breakAtr:F3} | " +
                    $"reason={(sharplyFadingExtendedBreakout ? "SHARP_FADE_EXTENDED" : "FADE_MATURE_BREAKOUT")}");
            return reject;
        }

        private static bool FiniteGoldValue(double v) =>
            !double.IsNaN(v) && !double.IsInfinity(v);

        // ============================================================
        // LIVE FAILURE GUARD: FIVE-MINUTE QUOTE HISTORY
        // ============================================================
        // Samples one midpoint per 10 seconds, independently per symbol.
        // Returns null until a real sample from approximately five minutes
        // earlier is available. Never fabricates a prior price from H1 bars.
        private sealed class ImpulsePriceSample
        {
            public DateTime Utc;
            public double Mid;
        }

        private static readonly object ImpulsePriceHistorySync = new object();
        private static readonly System.Collections.Generic.Dictionary<string,
            System.Collections.Generic.List<ImpulsePriceSample>> ImpulsePriceHistory =
                new System.Collections.Generic.Dictionary<string,
                    System.Collections.Generic.List<ImpulsePriceSample>>(
                        System.StringComparer.OrdinalIgnoreCase);

        private static double? ObserveFiveMinutePrice(string symbol, double mid)
        {
            if (string.IsNullOrWhiteSpace(symbol) || mid <= 0 ||
                double.IsNaN(mid) || double.IsInfinity(mid)) return null;

            DateTime now = DateTime.UtcNow;
            lock (ImpulsePriceHistorySync)
            {
                System.Collections.Generic.List<ImpulsePriceSample> samples;
                if (!ImpulsePriceHistory.TryGetValue(symbol, out samples))
                {
                    samples = new System.Collections.Generic.List<ImpulsePriceSample>();
                    ImpulsePriceHistory[symbol] = samples;
                }

                samples.RemoveAll(s => (now - s.Utc).TotalMinutes > 7.0);
                if (samples.Count == 0 ||
                    (now - samples[samples.Count - 1].Utc).TotalSeconds >= 10.0)
                {
                    samples.Add(new ImpulsePriceSample { Utc = now, Mid = mid });
                }

                DateTime target = now.AddMinutes(-5);
                ImpulsePriceSample closest = null;
                double smallestGap = double.MaxValue;
                foreach (ImpulsePriceSample sample in samples)
                {
                    double gap = Math.Abs((sample.Utc - target).TotalSeconds);
                    if (gap < smallestGap)
                    {
                        smallestGap = gap;
                        closest = sample;
                    }
                }

                // If the process has just started (or a data gap exists),
                // report 5-minute movement as UNKNOWN, not zero.
                return closest != null && smallestGap <= 30.0
                    ? (double?)closest.Mid : null;
            }
        }

        // Evaluate a candidate just before final approval. Return true ONLY for
        // an observed compound failure pattern; missing observations fail open.
        // This is a real live veto, not diagnostic/observation mode.
        private static bool RejectLiveImpulseFailureRisk(
            string symbol, TradeAction action, double atr,
            double bid, double ask, double? fiveMinuteMid,
            ObservedGoldH1 h1, double currentMomentum,
            double previousMomentum, double closedEma9,
            double closedEma21,
            SupportResistanceEngine.SrEntryDiagnostics sr)
        {
            if (!IsGold(symbol) || h1 == null ||
                (action != TradeAction.EnterBuy && action != TradeAction.EnterSell)) return false;

            try
            {
                double mid = (bid + ask) / 2.0;
                // The existing GOLD wick guard observes midpoint highs/lows.
                // Convert both that midpoint range and the latest quote to
                // the same price convention for internal wick geometry.
                // There is no invented OHLC from synthetic endpoint candles.
                var snapshot = new LiveImpulseFailurePredictor.Snapshot
                {
                    Symbol = symbol,
                    Side = action == TradeAction.EnterBuy
                        ? LiveImpulseFailurePredictor.Direction.Buy
                        : LiveImpulseFailurePredictor.Direction.Sell,
                    TimestampUtc = DateTime.UtcNow,
                    Atr = atr,
                    Bid = mid,
                    Ask = mid,
                    LiveOpen = h1.Open,
                    LiveHigh = Math.Max(h1.High, mid),
                    LiveLow = Math.Min(h1.Low, mid),
                    CurrentMomentum = Math.Abs(currentMomentum),
                    PreviousMomentum = Math.Abs(previousMomentum),
                    CompletedH1Ema9 = closedEma9,
                    CompletedH1Ema21 = closedEma21,
                    PriceFiveMinutesAgo = fiveMinuteMid,
                    // A rolling ten-H1 high/low is not necessarily a
                    // compression boundary. Leave unknown until a genuine
                    // compression detector provides this measurement.
                    CompressionBoundary = null,
                    OpposingWallClearanceAtr = sr != null && sr.WallAhead &&
                        !double.IsNaN(sr.ClearanceAtr) &&
                        !double.IsInfinity(sr.ClearanceAtr)
                        ? (double?)sr.ClearanceAtr : null
                };
                var result = LiveImpulseFailurePredictor.Evaluate(snapshot);
                bool reject = LiveImpulseFailurePredictor.ShouldRejectLiveEntry(result);
                LogFile.Write(result.ToLogLine() +
                    " | source=GOLD_FASTENTRY_LIVE_CANDIDATE" +
                    " | fiveMinAvailable=" + fiveMinuteMid.HasValue +
                    " | wallAhead=" + (sr != null && sr.WallAhead) +
                    " | decision=" + (reject ? "BLOCK" : "ALLOW"));
                return reject;
            }
            catch (Exception ex)
            {
                LogFile.Write($"[{symbol}] IMPULSE FAILURE LIVE GUARD ERROR | " +
                    $"type={ex.GetType().Name} | reason={ex.Message} | " +
                    "decision=ALLOW_UNVERIFIED | note=existing safety gates still apply");
                return false;
            }
        }

        // GOLD S/R IS ENFORCED FOR LIVE IMPULSE ENTRIES.
        // SupportResistanceEngine supplies the relaxed normal runway (0.30 ATR).
        // Keep the critical no-space, breakout, next-wall, and EMA protections.
        // Do not bypass S/R during live trading.
        private const bool GoldSrObservationOnly = false;

        private static bool ObserveGoldSrBlock(string symbol, bool blocked,
            TradeAction action, string path)
        {
            if (blocked && GoldSrObservationOnly && IsGold(symbol))
            {
                LogFile.Write($"[{symbol}] GOLD SR TEST BYPASS | path={path} | " +
                    $"action={action} | originalSrBlocked=True | executionChanged=True | " +
                    "mode=DEMO_ONLY_OBSERVATION");
                return false;
            }

            return blocked;
        }

        // ============================================================
        // GOLD IMPULSE: EXTENDED ENTRY INTO OPPOSING STRUCTURE
        // ============================================================
        // Supplements (never replaces) SupportResistanceEngine.ShouldBlockTrade.
        // The normal Gold 0.30 ATR runway is preserved for entries closer
        // to EMA9; only the COMBINATION of a nearby unbroken opposing wall
        // AND an extended entry gets an additional veto.
        // Symmetric: resistance ahead of BUY; support below SELL.
        // A wall already cleared by a genuine breakout is not "ahead".
        // This guard never approves an entry on its own.
        private const double GoldExtendedEntryEma9Atr = 0.90;
        private const double GoldExtendedEntryMinRunwayAtr = 0.60;

        private static bool RejectExtendedGoldEntryIntoWall(
            string symbol,
            TradeAction action,
            SupportResistanceEngine.SrEntryDiagnostics sr)
        {
            if (!IsGold(symbol) || sr == null ||
                (action != TradeAction.EnterBuy && action != TradeAction.EnterSell))
                return false;

            if (!sr.WallAhead || sr.Wall <= 0 ||
                double.IsNaN(sr.ClearanceAtr) || double.IsInfinity(sr.ClearanceAtr) ||
                double.IsNaN(sr.Ema9DistanceAtr) || double.IsInfinity(sr.Ema9DistanceAtr))
                return false;

            bool extended = sr.Ema9DistanceAtr >= GoldExtendedEntryEma9Atr;
            bool wallTooNear = sr.ClearanceAtr < GoldExtendedEntryMinRunwayAtr;
            if (!extended || !wallTooNear)
                return false;

            LogFile.Write($"[{symbol}] GOLD IMPULSE BLOCKED | " +
                $"reason=EXTENDED_ENTRY_INTO_OPPOSING_SR | action={action} | " +
                $"opposingWall={sr.Wall:F5} | runwayATR={sr.ClearanceAtr:F2} | " +
                $"requiredRunwayATR={GoldExtendedEntryMinRunwayAtr:F2} | " +
                $"ema9DistATR={sr.Ema9DistanceAtr:F2} | " +
                $"extendedAtATR={GoldExtendedEntryEma9Atr:F2}");
            return true;
        }

        // ============================================================
        // ENTRY STUDY DIAGNOSTICS
        // Frozen only when an entry is actually APPROVED.
        // ============================================================
        public sealed class EntryStudyDiagnostics
        {
            public double SrWall { get; set; }
            public double SrClearanceAtr { get; set; }
            public bool SrAhead { get; set; }
            public double Ema9DistanceAtr { get; set; }
        }

        // AsyncLocal isolates diagnostics per logical async execution context.
        // Evaluate and ConsumeApprovedEntryDiagnostics must run in the SAME context.
        private static readonly System.Threading.AsyncLocal<EntryStudyDiagnostics>
            _approvedEntryDiagnostics =
                new System.Threading.AsyncLocal<EntryStudyDiagnostics>();

        public static EntryStudyDiagnostics ConsumeApprovedEntryDiagnostics()
        {
            var result = _approvedEntryDiagnostics.Value;
            _approvedEntryDiagnostics.Value = null;
            return result;
        }

        private static void ApproveEntryDiagnostics(
            SupportResistanceEngine.SrEntryDiagnostics sr)
        {
            _approvedEntryDiagnostics.Value = sr == null
                ? null
                : new EntryStudyDiagnostics
                {
                    SrWall = sr.Wall,
                    SrClearanceAtr = sr.ClearanceAtr,
                    SrAhead = sr.WallAhead,
                    Ema9DistanceAtr = sr.Ema9DistanceAtr
                };
        }

        // GOLD authority: chop and final candidate rules are evaluated here,
        // never as a second independent TradeService strategy decision.
        public static (TradeAction, EntryModel) EvaluateGold(
            string symbol, string marketState,
            MqlRates[] rates, double atr, double[] emaFast, double[] emaSlow,
            double momentum, double previousMomentum,
            bool momentumRising, bool momentumFalling, bool strongMomentum,
            double bid, double ask, M15ConfirmationService m15Service,
            out TradeType tradeType, out bool isImpulse,
            out int emergingDirection)
        {
            tradeType = TradeType.None;
            isImpulse = false;
            emergingDirection = 0;
            // Closed-H1 assumptions match the existing TradeService GOLD scan.
            double liveOpen = rates != null && rates.Length > 0 ? rates[rates.Length - 1].close : 0;
            double liveMid = (bid + ask) * 0.5;
            var liveCandle = new MqlRates
            {
                open = liveOpen,
                high = Math.Max(liveOpen, liveMid),
                low = Math.Min(liveOpen, liveMid),
                close = liveMid
            };
            if (ShouldAvoidChoppyMarket(symbol, marketState, rates, emaFast,
                    emaSlow, atr, liveCandle, liveMid, momentum, out emergingDirection))
            {
                _approvedEntryDiagnostics.Value = null;
                LogFile.Write($"[{symbol}] GOLD FASTENTRY BLOCK | reason=CHOP_OR_UNCONFIRMED_ESCAPE");
                return (TradeAction.DoNothing, EntryModel.Nothing);
            }

            var result = Evaluate(symbol, rates, atr, emaFast, emaSlow, momentum,
                previousMomentum, momentumRising, momentumFalling, strongMomentum,
                bid, ask, m15Service, out tradeType, out isImpulse);

            if (result.Item1 != TradeAction.EnterBuy && result.Item1 != TradeAction.EnterSell)
                return result;

            if ((emergingDirection > 0 && result.Item1 != TradeAction.EnterBuy) ||
                (emergingDirection < 0 && result.Item1 != TradeAction.EnterSell))
            {
                LogFile.Write($"[{symbol}] GOLD FASTENTRY BLOCK | " +
                    $"reason=EMERGING_TREND_OPPOSITE | direction={emergingDirection} | action={result.Item1}");
                _approvedEntryDiagnostics.Value = null;
                tradeType = TradeType.None;
                isImpulse = false;
                return (TradeAction.DoNothing, EntryModel.Nothing);
            }

            if (RejectGoldWeakBoundaryEntry(symbol, result.Item1, rates, atr,
                    liveOpen, bid, ask, momentum, previousMomentum, result.Item2))
            {
                _approvedEntryDiagnostics.Value = null;
                tradeType = TradeType.None;
                isImpulse = false;
                return (TradeAction.DoNothing, EntryModel.Nothing);
            }
            return result;
        }

        // Compatibility: existing callers use the synthetic H1 fallback.
        public static (TradeAction, EntryModel) Evaluate(
            string symbol, MqlRates[] rates, double atr, double[] emaFast,
            double[] emaSlow, double momentum, double previousMomentum,
            bool momentumRising, bool momentumFalling, bool strongMomentum,
            double bid, double ask, M15ConfirmationService m15Service,
            out TradeType tradeType, out bool isImpulse)
        {
            return Evaluate(symbol, rates, atr, emaFast, emaSlow, momentum,
                previousMomentum, momentumRising, momentumFalling, strongMomentum,
                bid, ask, m15Service, out tradeType, out isImpulse,
                default(MqlRates), false);
        }

        public static (TradeAction, EntryModel) Evaluate(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double momentum,
            double previousMomentum,
            bool momentumRising,
            bool momentumFalling,
            bool strongMomentum,
            double bid,
            double ask,
            M15ConfirmationService _m15Service,
            out TradeType tradeType,
            out bool isImpulse,
            MqlRates brokerLiveH1,
            bool hasBrokerLiveH1)
        {
            isImpulse = false;
            tradeType = TradeType.None;

            // Never allow diagnostics from an earlier evaluation/trade
            // to leak into the current Gold decision.
            _approvedEntryDiagnostics.Value = null;

            try
            {
                // =========================================================
                // 1. BASIC SAFETY CHECKS
                // =========================================================

                if (rates == null ||
                    rates.Length < 20)
                {
                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );
                }

                if (atr <= 0)
                {
                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );
                }

                if (emaFast == null ||
                    emaFast.Length < 2 ||
                    emaSlow == null ||
                    emaSlow.Length < 1)
                {
                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );
                }

                if (bid <= 0 ||
                    ask <= 0 ||
                    ask < bid)
                {
                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );
                }


                _m15Service.Update(symbol);


                // =========================================================
                // 2. PREVIOUS CLOSED CANDLE
                //
                // rates[^1] is assumed to be the latest CLOSED candle.
                // =========================================================

                var previousCandle =
                    rates[^1];


                // =========================================================
                // 3. LIVE MARKET PRICE
                // =========================================================

                double liveMid =
                    (bid + ask) /
                    2.0;

                // Collect live quote history on every GOLD evaluation, not
                // only when a candidate passes the approval pipeline.
                double? goldFiveMinuteMid = IsGold(symbol)
                    ? ObserveFiveMinutePrice(symbol, liveMid)
                    : null;


                // =========================================================
                // 4. SYNTHETIC LIVE H1 CANDLE
                //
                // The current H1 candle is not in rates[].
                //
                // Therefore use previous H1 close as the synthetic
                // current H1 open.
                // =========================================================

                double liveOpen =
                    previousCandle.close;


                var liveCandle =
                    new MqlRates
                    {
                        open = liveOpen,

                        high =
                            Math.Max(
                                liveOpen,
                                liveMid
                            ),

                        low =
                            Math.Min(
                                liveOpen,
                                liveMid
                            ),

                        close =
                            liveMid
                    };


                // Prefer the CURRENT MT5 H1 bar over synthetic endpoint-only data.
                // This is enabled only when TradeService supplies shift=0 and
                // the bar timestamp is newer than the last closed H1 bar.
                bool usingBrokerLiveH1 =
                    hasBrokerLiveH1 &&
                    brokerLiveH1.time > previousCandle.time &&
                    brokerLiveH1.open > 0 &&
                    brokerLiveH1.high >= brokerLiveH1.low &&
                    brokerLiveH1.low > 0 &&
                    brokerLiveH1.high >= brokerLiveH1.open &&
                    brokerLiveH1.low <= brokerLiveH1.open &&
                    liveMid <= brokerLiveH1.high + Math.Max(atr * 0.05, ask - bid) &&
                    liveMid >= brokerLiveH1.low - Math.Max(atr * 0.05, ask - bid);

                if (usingBrokerLiveH1)
                {
                    liveOpen = brokerLiveH1.open;
                    liveCandle = brokerLiveH1;
                    // Keep direction/body aligned to the current quote.
                    liveCandle.close = liveMid;
                    liveCandle.high = Math.Max(liveCandle.high, liveMid);
                    liveCandle.low = Math.Min(liveCandle.low, liveMid);
                }

                // DIAGNOSTIC ONLY: high/low are endpoints, NOT intrabar extremes.
                // Never infer a real rejection wick from this synthetic candle.
                // LiveImpulseEntryEngine must use its synthetic-wick-safe guards.
                if (IsGold(symbol) && !usingBrokerLiveH1)
                {
                    LogFile.Write(
                        $"[{symbol}] GOLD SYNTHETIC H1 INPUT | " +
                        $"source=PREVIOUS_CLOSED_H1_CLOSE_TO_LIVE_MID | " +
                        $"liveOpen={liveOpen:F5} | liveMid={liveMid:F5} | " +
                        $"syntheticHigh={liveCandle.high:F5} | " +
                        $"syntheticLow={liveCandle.low:F5} | " +
                        $"hasRealIntrabarExtremes=False | " +
                        $"note=Do not use synthetic high/low for wick rejection");
                }

                // Observe H1 high/low internally, with no changes to either Evaluate signature.
                // When an actual broker H1 bar is already available, its real
                // extremes take precedence over locally observed prices.
                var observedGoldH1 = ObserveGoldLiveH1(symbol, previousCandle, liveMid);
                if (usingBrokerLiveH1)
                {
                    observedGoldH1.Open = liveCandle.open;
                    observedGoldH1.High = liveCandle.high;
                    observedGoldH1.Low = liveCandle.low;
                }

                // =========================================================
                // 5. CLOSED EMA VALUES
                //
                // emaFast[^1] / emaSlow[^1] belong to the latest CLOSED
                // candle.
                //
                // Previously these closed values were being passed
                // directly into the LIVE impulse engine.
                //
                // That made EMA direction stale for the entire current
                // H1 candle.
                // =========================================================

                double closedEma9 =
                    emaFast[^1];


                double priorClosedEma9 =
                    emaFast[^2];


                double closedEma21 =
                    emaSlow[^1];


                // =========================================================
                // 6. LIVE EMA ESTIMATES
                //
                // EMA:
                //
                // currentEMA =
                // previousEMA + alpha * (livePrice - previousEMA)
                //
                // alpha = 2 / (period + 1)
                //
                // EMA9  alpha = 0.20
                // EMA21 alpha ≈ 0.090909
                //
                // IMPORTANT:
                //
                // For live slope:
                //
                // ema9Previous = latest CLOSED EMA9
                // ema9         = current LIVE EMA9
                //
                // Therefore:
                //
                // ema9 < ema9Previous
                //
                // can become true DURING the current candle instead of
                // waiting until the H1 candle closes.
                // =========================================================

                double ema9Previous =
                    closedEma9;


                double ema9 =
                    CalculateLiveEma(
                        closedEma9,
                        liveMid,
                        9
                    );


                double ema21 =
                    CalculateLiveEma(
                        closedEma21,
                        liveMid,
                        21
                    );


                // =========================================================
                // 7. LIVE-ADJUSTED EMA ARRAYS
                //
                // Do NOT mutate the arrays supplied by the caller.
                //
                // Create copies and replace only the latest EMA value
                // with the live estimate.
                //
                // Slower downstream engines can therefore respond to the
                // current candle too.
                // =========================================================

                double[] liveEmaFast =
                    (double[])emaFast.Clone();


                double[] liveEmaSlow =
                    (double[])emaSlow.Clone();


                liveEmaFast[
                    liveEmaFast.Length - 1
                ] = ema9;


                liveEmaSlow[
                    liveEmaSlow.Length - 1
                ] = ema21;


                // =========================================================
                // 8. RECENT CLOSED STRUCTURE
                //
                // IMPORTANT:
                //
                // Structure remains based on CLOSED candles.
                //
                // Do NOT include the synthetic live candle, otherwise the
                // breakout level moves with price and becomes impossible
                // to break cleanly.
                // =========================================================

                double recentHigh =
                    rates
                        .Skip(
                            Math.Max(
                                0,
                                rates.Length - 10
                            )
                        )
                        .Max(
                            r => r.high
                        );


                double recentLow =
                    rates
                        .Skip(
                            Math.Max(
                                0,
                                rates.Length - 10
                            )
                        )
                        .Min(
                            r => r.low
                        );


                // Active stateful GOLD continuation evaluation (BUY and SELL).
                // It offers a candidate only after a confirmed EMA9 pullback and
                // resumption; existing Impulse/armed/swing detections have priority.
                TradeAction advisorCandidate = EvaluateGoldTrendContinuation(
                    symbol, previousCandle, liveCandle, atr, bid, ask,
                    ema9, closedEma9, ema21, closedEma21,
                    recentHigh, recentLow, _m15Service);

                // =========================================================
                // 9. LIVE EMA DIAGNOSTICS
                //
                // This is extremely important after yesterday.
                //
                // If gold moves violently and these values don't change,
                // we immediately know where the data problem is.
                // =========================================================

                bool liveEma9Rising =
                    ema9 >
                    ema9Previous;


                bool liveEma9Falling =
                    ema9 <
                    ema9Previous;


                LogFile.Write(
                    $"[{symbol}] 📡 LIVE EMA | " +

                    $"price={liveMid:F5} | " +

                    $"closedEma9={closedEma9:F5} | " +
                    $"priorClosedEma9={priorClosedEma9:F5} | " +

                    $"liveEma9={ema9:F5} | " +
                    $"liveEma21={ema21:F5} | " +

                    $"liveEma9Rising={liveEma9Rising} | " +
                    $"liveEma9Falling={liveEma9Falling}"
                );


                // =========================================================
                // 10. IMPULSE ENTRY — FIRST PRIORITY
                //
                // IMPORTANT:
                //
                // Impulse gets:
                //
                // LIVE candle
                // LIVE EMA9
                // LIVE EMA9 slope
                // LIVE EMA21
                // CLOSED structure
                //
                // This combination is what we actually want.
                // =========================================================

                LogFile.Write(
                    $"[{symbol}] >>> CALLING IMPULSE ENGINE | " +

                    $"liveOpen={liveCandle.open:F5} | " +

                    $"bid={bid:F5} | " +
                    $"ask={ask:F5} | " +

                    $"atr={atr:F5} | " +

                    $"ema9={ema9:F5} | " +
                    $"ema9Prev={ema9Previous:F5} | " +
                    $"ema21={ema21:F5} | " +

                    $"closedEma9={closedEma9:F5} | " +
                    $"closedEma21={closedEma21:F5} | " +

                    $"recentHigh={recentHigh:F5} | " +
                    $"recentLow={recentLow:F5} | " +

                    $"momentum={momentum:F2}"
                );

                // Gold observation-only snapshots for acceleration timing study.
                // Does not change signals, support/resistance, or order execution.
                try
                {
                    GoldEntryTimingShadowDiagnostics.Observe(
                        symbol, bid, ask, liveOpen, atr, ema9, ema9Previous,
                        ema21, recentHigh, recentLow, momentum);
                }
                catch (Exception)
                {
                    // Diagnostic failures must never change an entry decision.
                }

                // Initialized even when GOLD SR observation mode skips the
                // armed-breakdown call (short-circuit evaluation).
                TradeAction armedGoldAction = TradeAction.Hold;

                bool goldBreakdownTriggered =
                    !(GoldSrObservationOnly && IsGold(symbol)) &&
                    SupportResistanceEngine.TryTriggerArmedGoldBreakdown(
                        symbol,
                        atr,
                        liveMid,
                        ema9,
                        ema9Previous,
                        momentum,
                        out armedGoldAction
                    );


                bool impulseDetected;
                TradeAction impulseAction;


                if (goldBreakdownTriggered)
                {
                    impulseDetected =
                        true;

                    impulseAction =
                        armedGoldAction;

                    LogFile.Write(
                        $"[{symbol}] 🔥 ARMED GOLD STRUCTURAL BREAK PROMOTED TO IMPULSE | " +
                        $"action={impulseAction} | " +
                        $"price={liveMid:F5} | " +
                        $"ema9={ema9:F5} | " +
                        $"ema9Prev={ema9Previous:F5} | " +
                        $"momentum={momentum:F2}"
                    );
                }
                else
                {
                    impulseDetected =
                        LiveImpulseEntryEngine.TryEnter(
                            symbol,
                            liveCandle,
                            atr,
                            ema9,
                            ema9Previous,
                            ema21,
                            recentHigh,
                            recentLow,
                            bid,
                            ask,
                            momentum,
                            out impulseAction
                        );
                }


                if (IsGold(symbol))
                {
                    double diagnosticBodyAtr = Math.Abs(liveMid - liveOpen) / atr;
                    double diagnosticEmaDistAtr = Math.Abs(liveMid - ema9) / atr;
                    double diagnosticBuyBreakAtr = (ask - recentHigh) / atr;
                    double diagnosticSellBreakAtr = (recentLow - bid) / atr;
                    LogFile.Write(
                        $"[{symbol}] GOLD IMPULSE INPUT SUMMARY | " +
                        $"bodyATR={diagnosticBodyAtr:F3} | " +
                        $"momentum={momentum:F3} | previousMomentum={previousMomentum:F3} | " +
                        $"momentumRising={momentumRising} | momentumFalling={momentumFalling} | " +
                        $"emaDistATR={diagnosticEmaDistAtr:F3} | " +
                        $"emaSpreadATR={Math.Abs(ema9 - ema21) / atr:F3} | " +
                        $"ema9SlopeATR={Math.Abs(ema9 - ema9Previous) / atr:F3} | " +
                        $"buyBreakATR={diagnosticBuyBreakAtr:F3} | " +
                        $"sellBreakATR={diagnosticSellBreakAtr:F3} | " +
                        $"bullRegime={ema9 > ema21 && ema9 > ema9Previous} | " +
                        $"bearRegime={ema9 < ema21 && ema9 < ema9Previous}");
                }

                // =========================================================
                // GOLD LIVE SWING STRUCTURE - ADDITIVE ENTRY PATH
                // =========================================================
                // Preserve normal impulse and armed-breakdown candidates.
                // Only offer an EXTRA live swing-break candidate when neither
                // existing detector has produced one. This is NOT a veto.
                // The candidate must still pass the original follow-through,
                // opposite-H1, GOLD hard-SR, M15, wick and broker entry checks.
                bool swingStructureCandidate = false;
                if (!impulseDetected && IsGold(symbol))
                {
                    var swingBreak = SwingStructureEngine.TryDetectLiveBreak(
                        symbol, rates, atr, bid, ask, liveCandle.open,
                        ema9, ema21, momentum);

                    if (swingBreak.HasSignal)
                    {
                        impulseDetected = true;
                        impulseAction = swingBreak.Action;
                        swingStructureCandidate = true;

                        LogFile.Write(
                            $"[{symbol}] GOLD LIVE SWING BREAK CANDIDATE | " +
                            $"action={impulseAction} | level={swingBreak.BrokenLevel:F5} | " +
                            $"extensionATR={swingBreak.ExtensionAtr:F2} | " +
                            $"bodyATR={swingBreak.BodyAtr:F2} | " +
                            $"structure={swingBreak.Structure?.Direction} | " +
                            "status=REQUIRES_EXISTING_GOLD_FASTENTRY_GATES");
                    }
                }

                // GOLD LIVE PULLBACK-CONTINUATION — additive candidate only.
                // It does NOT bypass the existing post-entry approval pipeline.
                bool goldAdvisorContinuationCandidate = false;
                if (!impulseDetected && IsGold(symbol) &&
                    (advisorCandidate == TradeAction.EnterBuy ||
                     advisorCandidate == TradeAction.EnterSell) &&
                    Math.Abs(momentum) >= 0.60)
                {
                    impulseDetected = true;
                    impulseAction = advisorCandidate;
                    goldAdvisorContinuationCandidate = true;
                    LogFile.Write($"[{symbol}] GOLD LIVE CONTINUATION CANDIDATE | " +
                        $"action={impulseAction} | momentum={momentum:F2} | " +
                        "source=PULLBACK_RESUMPTION | status=REQUIRES_ALL_EXISTING_FASTENTRY_GATES");
                }

                LogFile.Write(
                    $"[{symbol}] <<< IMPULSE ENGINE RESULT | " +
                    $"detected={impulseDetected} | " +
                    $"action={impulseAction} | " +
                    $"goldBreakdownTriggered={goldBreakdownTriggered}"
                );



                // GOLD: an armed structural break or a newly detected swing
                // break must not circumvent the same anti-chase location rule
                // that governs normal LiveImpulseEntryEngine entries.
                // Do not change normal impulse candidates or any exit rules.
                if (impulseDetected && (swingStructureCandidate || goldBreakdownTriggered || goldAdvisorContinuationCandidate) &&
                    !LiveImpulseEntryEngine.GoldAlternativeLocationIsSafe(
                        symbol, liveCandle, atr, ema9, ema9Previous, ema21,
                        recentHigh, recentLow, bid, ask, impulseAction))
                {
                    LogFile.Write($"[{symbol}] GOLD STRUCTURAL ENTRY REJECTED | " +
                        $"path={(goldAdvisorContinuationCandidate ? "TREND_CONTINUATION" :
                            swingStructureCandidate ? "SWING_BREAK" : "ARMED_BREAKDOWN")} | " +
                        "reason=ANTI_CHASE_LOCATION");
                    tradeType = TradeType.None;
                    isImpulse = false;
                    return (TradeAction.DoNothing, EntryModel.Nothing);
                }

                // Evaluate the confirmed candidate before follow-through and
                // before ANY of the alternate GOLD paths can approve it.
                if (impulseDetected && IsGold(symbol) &&
                    RejectGoldExtendedFadingEntry(
                        symbol, impulseAction, atr, bid, ask, liveOpen,
                        ema9, recentHigh, recentLow, momentum, previousMomentum,
                        goldAdvisorContinuationCandidate ? "TREND_CONTINUATION" :
                        swingStructureCandidate ? "SWING_BREAK" :
                        goldBreakdownTriggered ? "ARMED_BREAKDOWN" : "LIVE_IMPULSE"))
                {
                    tradeType = TradeType.None;
                    isImpulse = false;
                    return (TradeAction.DoNothing, EntryModel.Nothing);
                }

                // =========================================================
                // 11. IMPULSE DETECTED
                // =========================================================

                if (impulseDetected)
                {
                    // =====================================================
                    // STRONG OPPOSITE-CANDLE SCENARIO FILTER - IMPULSE ONLY
                    //
                    // This rule activates ONLY when the latest CLOSED H1 candle
                    // is a strong candle in the opposite direction of the
                    // proposed impulse entry.
                    //
                    // BUY:
                    // - latest closed H1 candle is bearish
                    // - bearish candle body is strong relative to ATR
                    // - then block the immediate BUY impulse
                    //
                    // SELL:
                    // - latest closed H1 candle is bullish
                    // - bullish candle body is strong relative to ATR
                    // - then block the immediate SELL impulse
                    //
                    // If that exact strong-opposite-candle situation has NOT
                    // occurred, FastEntryEngine continues normally.
                    //
                    // Other entry paths are NOT affected.
                    // =====================================================

                    //bool followThroughAllowed =
                    //    FollowThroughEntryEngine.AllowEntry(
                    //        symbol,
                    //        impulseAction,
                    //        momentum,
                    //        momentumRising,
                    //        momentumFalling,
                    //        strongMomentum
                    //    );


                    // ========================================================
                    // FOLLOW-THROUGH INPUTS
                    // ========================================================

                    double emaDistanceAtr =
                        atr > 0.0
                            ? Math.Abs(liveMid - ema9) / atr
                            : 0.0;


                    double bodyAtr =
                        atr > 0.0
                            ? Math.Abs(liveMid - liveCandle.open) / atr
                            : 0.0;


                    // A live break of a confirmed swing pivot is a valid
                    // structural breakout even when the pivot differs from
                    // the rolling ten-bar high/low. Existing impulses keep
                    // their original breakout calculation unchanged.
                    bool confirmedBreakout =
                        swingStructureCandidate ||
                        (impulseAction == TradeAction.EnterBuy
                            ? ask > recentHigh
                            : bid < recentLow);

                    bool followThroughAllowed =
                    FollowThroughEntryEngine.AllowEntry(
                        symbol,
                        impulseAction,
                        momentum,
                        previousMomentum,
                        momentumRising,
                        momentumFalling,
                        strongMomentum,
                        emaDistanceAtr,
                        bodyAtr,
                        confirmedBreakout
                    );

                    if (!followThroughAllowed)
                    {
                        LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | reason=FOLLOW_THROUGH | action={impulseAction} | momentum={momentum:F2} | previousMomentum={previousMomentum:F2} | rising={momentumRising} | falling={momentumFalling} | strong={strongMomentum} | emaDistATR={emaDistanceAtr:F2} | bodyATR={bodyAtr:F2} | breakout={confirmedBreakout}");
                        tradeType = TradeType.None;
                        isImpulse = false;

                        return (
                            TradeAction.DoNothing,
                            EntryModel.Nothing
                        );
                    }

                    if (!PassesStrongOppositeCandleScenarioFilter(
                            symbol,
                            previousCandle,
                            atr,
                            impulseAction,
                            liveMid,
                            ema9,
                            ema9Previous,
                            ema21,
                            momentum))
                    {
                        LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | reason=OPPOSITE_H1_CANDLE | action={impulseAction} | closedOpen={previousCandle.open:F5} | closedClose={previousCandle.close:F5} | momentum={momentum:F2}");
                        tradeType = TradeType.None;
                        isImpulse = false;

                        return (
                            TradeAction.DoNothing,
                            EntryModel.Nothing
                        );
                    }

                    tradeType =
                        TradeType.Breakout;


                    double executionPrice =
                        impulseAction ==
                            TradeAction.EnterBuy
                                ? ask
                                : bid;

                    double impulseOverrideThreshold =
                        IsFx(symbol)
                            ? 0.60
                            : 1.20;
                    // =====================================================
                    // 11A. SUPPORT / RESISTANCE
                    //
                    // Strong impulse can override SR.
                    // =====================================================

                    SupportResistanceEngine.SrEntryDiagnostics impulseSrDiagnostics;

                    bool srBlocked =
                        SupportResistanceEngine.ShouldBlockTrade(
                            symbol,
                            impulseAction,
                            rates,
                            atr,
                            momentum,
                            executionPrice,
                            ema9,
                            out impulseSrDiagnostics,
                            recentHigh,
                            recentLow,
                            isImpulseEntry: true,
                            isArmedBreakdownTrigger: goldBreakdownTriggered
                        );


                    if (ObserveGoldSrBlock(symbol, srBlocked, impulseAction, "IMPULSE"))
                    {
                        bool allowStrongImpulseThroughSr =
                             !IsGold(symbol) && momentum >= impulseOverrideThreshold;


                        if (!allowStrongImpulseThroughSr)
                        {
                            LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | reason=SR | action={impulseAction} | momentum={momentum:F2} | overrideAt={impulseOverrideThreshold:F2} | price={executionPrice:F5} | emaDistATR={emaDistanceAtr:F2}");
                            LogFile.Write(
                                $"[{symbol}] ❌ IMPULSE BLOCKED BY SR | " +
                                $"action={impulseAction} | " +
                                $"price={executionPrice:F5} | " +
                                $"momentum={momentum:F2}"
                            );


                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        LogFile.Write(
                            $"[{symbol}] ⚡ SR OVERRIDDEN FOR STRONG IMPULSE | " +
                            $"action={impulseAction} | " +
                            $"price={executionPrice:F5} | " +
                            $"momentum={momentum:F2}"
                        );
                    }


                    // The existing SR gate may allow 0.30-0.60 ATR of
                    // runway. For GOLD, reject that narrower runway ONLY
                    // when the entry is also extended from EMA9. This
                    // applies to both BUY and SELL without changing impulse
                    // detection, structural-break watches, or M15 rules.
                    if (RejectExtendedGoldEntryIntoWall(
                            symbol, impulseAction, impulseSrDiagnostics))
                    {
                        tradeType = TradeType.None;
                        isImpulse = false;
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    // =====================================================
                    // 11B. M15 CLOSED-CANDLE CONFIRMATION
                    //
                    // M15ConfirmationService.Update(symbol) builds its
                    // Bullish/Bearish state from rates[^1], the latest
                    // CLOSED M15 candle.
                    //
                    // Normal impulse:
                    //     BUY  -> M15 Bullish must be true
                    //     SELL -> M15 Bearish must be true
                    //
                    // GOLD (XAUUSD.s/XAUEUR.s): NO exception. M15 must confirm.
                    // Non-GOLD retains its existing strong-impulse exception.
                    // =====================================================

                    var m15 =
                        _m15Service.GetSnapshot(
                            symbol
                        );


                    // GOLD (XAUUSD.s and XAUEUR.s): closed M15 directional confirmation
                    // is mandatory. H1 momentum, structural breaks, or regime
                    // transitions must never override a missing/opposing M15.
                    // Preserve the original high-momentum exception for NON-GOLD.
                    bool allowStrongImpulseThroughM15 =
                        !IsGold(symbol) && momentum >= impulseOverrideThreshold;

                    if (m15 == null)
                    {
                        if (!allowStrongImpulseThroughM15)
                        {
                            LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | reason=M15_NO_SNAPSHOT | action={impulseAction} | momentum={momentum:F2}");
                            LogFile.Write(
                                $"[{symbol}] ❌ IMPULSE BLOCKED - NO CLOSED M15 CONFIRMATION | " +
                                $"action={impulseAction} | momentum={momentum:F2}"
                            );

                            tradeType = TradeType.None;
                            isImpulse = false;
                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }

                        LogFile.Write(
                            $"[{symbol}] 🔥 NO M15 SNAPSHOT OVERRIDDEN | " +
                            $"action={impulseAction} | momentum={momentum:F2} | " +
                            $"reason=Exceptional H1 impulse"
                        );
                    }
                    else
                    {
                        bool m15ConfirmsBuy =
                            impulseAction == TradeAction.EnterBuy &&
                            m15.Bullish;


                        bool m15ConfirmsSell =
                            impulseAction == TradeAction.EnterSell &&
                            m15.Bearish;


                        bool m15ConfirmsDirection =
                            m15ConfirmsBuy ||
                            m15ConfirmsSell;


                        if (!m15ConfirmsDirection)
                        {
                            if (!allowStrongImpulseThroughM15)
                            {
                                LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | reason=M15_NOT_CONFIRMED | action={impulseAction} | bullish={m15.Bullish} | bearish={m15.Bearish} | momentum={momentum:F2}");
                                LogFile.Write(
                                    $"[{symbol}] ❌ IMPULSE BLOCKED - CLOSED M15 DOES NOT CONFIRM | " +
                                    $"action={impulseAction} | " +
                                    $"M15Bullish={m15.Bullish} | " +
                                    $"M15Bearish={m15.Bearish} | " +
                                    $"M15CandleTime={m15.CandleTime} | " +
                                    $"momentum={momentum:F2}"
                                );

                                tradeType = TradeType.None;
                                isImpulse = false;
                                return (
                                    TradeAction.DoNothing,
                                    EntryModel.Nothing
                                );
                            }


                            LogFile.Write(
                                $"[{symbol}] 🔥 CLOSED M15 NON-CONFIRMATION OVERRIDDEN | " +
                                $"action={impulseAction} | " +
                                $"M15Bullish={m15.Bullish} | " +
                                $"M15Bearish={m15.Bearish} | " +
                                $"M15CandleTime={m15.CandleTime} | " +
                                $"momentum={momentum:F2} | " +
                                $"reason=Exceptional H1 impulse/regime transition"
                            );
                        }
                        else
                        {
                            LogFile.Write(
                                $"[{symbol}] ✅ CLOSED M15 CONFIRMS IMPULSE | " +
                                $"action={impulseAction} | " +
                                $"M15Bullish={m15.Bullish} | " +
                                $"M15Bearish={m15.Bearish} | " +
                                $"M15CandleTime={m15.CandleTime} | " +
                                $"momentum={momentum:F2}"
                            );
                        }
                    }


                    // =====================================================
                    // 11C. IMPULSE APPROVED
                    // =====================================================

                    isImpulse =
                        true;


                    // Keep the verified original GOLD wall available to the
                    // final entry guard. TradeService clears the watch only
                    // after the broker accepts the resulting order.


                    // Directional current-H1 wick protection (same thresholds as CASH/FX).
                    if (!PassesObservedGoldH1WickGuard(symbol, observedGoldH1, atr, bid, ask, impulseAction))
                    {
                        tradeType = TradeType.None;
                        isImpulse = false;
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    // LIVE impulse failure protection, after existing M15, S/R,
                    // follow-through and wick checks, before entry approval.
                    // A score by itself never vetoes: only a verified compound
                    // exhaustion/rejection pattern can block this candidate.
                    if (RejectLiveImpulseFailureRisk(
                        symbol, impulseAction, atr, bid, ask,
                        goldFiveMinuteMid, observedGoldH1,
                        momentum, previousMomentum,
                        closedEma9, closedEma21,
                        impulseSrDiagnostics))
                    {
                        LogFile.Write($"[{symbol}] GOLD IMPULSE ENTRY BLOCKED | " +
                            $"action={impulseAction} | " +
                            "reason=LIVE_PREDICTED_EXHAUSTION_AND_REJECTION");
                        tradeType = TradeType.None;
                        isImpulse = false;
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    ApproveEntryDiagnostics(impulseSrDiagnostics);

                    LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | reason=ALL_GATES_PASSED | action={impulseAction} | momentum={momentum:F2} | armedGoldBreakdown={goldBreakdownTriggered}");
                    LogFile.Write(
                        $"[{symbol}] ✅ IMPULSE ENTRY APPROVED | " +
                        $"action={impulseAction} | " +
                        $"price={executionPrice:F5} | " +
                        $"ema9={ema9:F5} | " +
                        $"ema21={ema21:F5} | " +
                        $"srAhead={impulseSrDiagnostics.WallAhead} | " +
                        $"srWall={impulseSrDiagnostics.Wall:F5} | " +
                        $"srClearanceATR={impulseSrDiagnostics.ClearanceAtr:F2} | " +
                        $"ema9DistATR={impulseSrDiagnostics.Ema9DistanceAtr:F2}"
                    );


                    return (
                        impulseAction,
                        EntryModel.ImpulseBreakout
                    );
                }

                // =========================================================
                // 13. GLOBAL MOMENTUM GATE
                //
                // Impulse has already had its opportunity.
                //
                // Therefore this only blocks slower engines.
                // =========================================================

                const double NormalEntryMomentumThreshold = 0.60;

                if (momentum < NormalEntryMomentumThreshold)
                {
                    LogFile.Write(
                       $"Normal entry engines blocked | " +
                       $"momentum={momentum:F2} < " +
                       $"{NormalEntryMomentumThreshold:F2} | " +
                       $"Impulse already evaluated={impulseDetected}");


                    tradeType =
                        TradeType.None;


                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );
                }


                // =========================================================
                // 14. MARKET STATE
                //
                // IMPORTANT:
                //
                // Use LIVE-adjusted EMA arrays.
                //
                // This prevents MarketState from being stuck in yesterday's
                // previous bullish H1 interpretation while current price
                // is collapsing.
                // =========================================================

                var state =
                    MarketStateEngine.DetectState(
                        symbol,
                        rates,
                        liveEmaFast,
                        liveEmaSlow,
                        atr
                    );


                // =========================================================
                // 18. NOTHING QUALIFIED
                // =========================================================

                return (
                    TradeAction.DoNothing,
                    EntryModel.Nothing
                );
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"[{symbol}] FastEntryEngine error: " +
                    $"{ex.Message}"
                );


                isImpulse =
                    false;


                tradeType =
                    TradeType.None;


                return (
                    TradeAction.DoNothing,
                    EntryModel.Nothing
                );
            }
        }

        private static bool IsFx(
          string symbol)
        {
            if (symbol.Length < 6)
                return false;


            string[] ccy =
            {
    "USD",
    "EUR",
    "GBP",
    "JPY",
    "AUD",
    "NZD",
    "CAD",
    "CHF"
};


            foreach (var baseCcy in ccy)
            {
                foreach (var quoteCcy in ccy)
                {
                    if (baseCcy ==
                        quoteCcy)
                    {
                        continue;
                    }


                    if (symbol.StartsWith(baseCcy) &&
                        symbol.Contains(quoteCcy))
                    {
                        return true;
                    }
                }
            }


            return false;
        }

        private static bool IsGold(string symbol)
        {
            symbol = symbol?.ToUpperInvariant() ?? "";

            return
                symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER");
        }
        // =============================================================
        // STRONG OPPOSITE-CANDLE SCENARIO FILTER - IMPULSE ONLY
        //
        // This filter is intentionally narrow.
        //
        // It does NOTHING unless the latest CLOSED H1 candle is:
        //
        // BUY impulse:
        //     a strong bearish candle
        //
        // SELL impulse:
        //     a strong bullish candle
        //
        // Strong is measured by real-body size relative to ATR.
        //
        // If there is no strong opposite candle, FastEntry runs normally.
        // =============================================================

        private static bool PassesStrongOppositeCandleScenarioFilter(
            string symbol,
            MqlRates previousClosedCandle,
            double atr,
            TradeAction action,
            double livePrice,
            double ema9,
            double ema9Previous,
            double ema21,
            double momentum,
            double strongOppositeBodyAtr = 0.60)
        {
            if (atr <= 0)
                return true;

            double body =
                Math.Abs(
                    previousClosedCandle.close -
                    previousClosedCandle.open
                );

            double bodyAtr =
                body / atr;

            bool previousBearish =
                previousClosedCandle.close <
                previousClosedCandle.open;

            bool previousBullish =
                previousClosedCandle.close >
                previousClosedCandle.open;

            bool strongBearishOpposite =
                action == TradeAction.EnterBuy &&
                previousBearish &&
                bodyAtr >= strongOppositeBodyAtr;

            bool strongBullishOpposite =
                action == TradeAction.EnterSell &&
                previousBullish &&
                bodyAtr >= strongOppositeBodyAtr;

            // ---------------------------------------------------------
            // No special scenario detected:
            // let FastEntryEngine continue exactly as normal.
            // ---------------------------------------------------------
            if (!strongBearishOpposite &&
                !strongBullishOpposite)
            {
                return true;
            }

            // ---------------------------------------------------------
            // CURRENT REVERSAL OVERRIDE
            //
            // Do not let the previous opposite H1 candle veto a fresh
            // impulse when the CURRENT live move has already demonstrated
            // a meaningful reversal:
            //
            // BUY  -> price reclaimed EMA21, EMA9 is rising, current live
            //         bullish body is >= 0.80 ATR and momentum >= 0.60.
            //
            // SELL -> price lost EMA21, EMA9 is falling, current live
            //         bearish body is >= 0.80 ATR and momentum >= 0.60.
            //
            // This keeps the old anti-whipsaw protection for weak/uncertain
            // impulses, but stops stale H1 direction from cancelling a
            // genuinely confirmed live reversal.
            // ---------------------------------------------------------

            double liveDirectionalBodyAtr = 0.0;

            if (action == TradeAction.EnterBuy)
            {
                liveDirectionalBodyAtr =
                    Math.Max(
                        0.0,
                        livePrice - previousClosedCandle.close
                    ) / atr;
            }
            else if (action == TradeAction.EnterSell)
            {
                liveDirectionalBodyAtr =
                    Math.Max(
                        0.0,
                        previousClosedCandle.close - livePrice
                    ) / atr;
            }

            bool currentReversalConfirmed =
                action == TradeAction.EnterBuy
                    ? livePrice > ema21 &&
                      ema9 > ema9Previous &&
                      liveDirectionalBodyAtr >= 0.80 &&
                      Math.Abs(momentum) >= 0.60
                    : livePrice < ema21 &&
                      ema9 < ema9Previous &&
                      liveDirectionalBodyAtr >= 0.80 &&
                      Math.Abs(momentum) >= 0.60;

            if (currentReversalConfirmed)
            {
                LogFile.Write(
                    $"[{symbol}] ✅ OPPOSITE H1 CANDLE OVERRIDDEN | " +
                    $"action={action} | " +
                    $"previousBodyATR={bodyAtr:F2} | " +
                    $"liveBodyATR={liveDirectionalBodyAtr:F2} | " +
                    $"momentum={momentum:F2} | " +
                    $"ema9={ema9:F5} | " +
                    $"ema9Previous={ema9Previous:F5} | " +
                    $"ema21={ema21:F5} | " +
                    $"livePrice={livePrice:F5} | " +
                    $"reason=current live reversal has already reclaimed/lost EMA21 with EMA9 confirmation"
                );

                return true;
            }

            // ---------------------------------------------------------
            // Special scenario detected:
            // block immediate impulse re-entry in the old trend direction.
            // ---------------------------------------------------------
            if (strongBearishOpposite)
            {
                LogFile.Write(
                    $"[{symbol}] ⛔ IMPULSE BUY BLOCKED | " +
                    $"reason=Strong bearish H1 candle before BUY impulse | " +
                    $"bodyATR={bodyAtr:F2} | " +
                    $"threshold={strongOppositeBodyAtr:F2} | " +
                    $"open={previousClosedCandle.open:F5} | " +
                    $"close={previousClosedCandle.close:F5} | " +
                    $"high={previousClosedCandle.high:F5} | " +
                    $"low={previousClosedCandle.low:F5}"
                );

                return false;
            }

            if (strongBullishOpposite)
            {
                LogFile.Write(
                    $"[{symbol}] ⛔ IMPULSE SELL BLOCKED | " +
                    $"reason=Strong bullish H1 candle before SELL impulse | " +
                    $"bodyATR={bodyAtr:F2} | " +
                    $"threshold={strongOppositeBodyAtr:F2} | " +
                    $"open={previousClosedCandle.open:F5} | " +
                    $"close={previousClosedCandle.close:F5} | " +
                    $"high={previousClosedCandle.high:F5} | " +
                    $"low={previousClosedCandle.low:F5}"
                );

                return false;
            }

            return true;
        }


        // =============================================================
        // LIVE EMA CALCULATION
        //
        // Calculates the current incomplete-candle EMA from:
        //
        // previous CLOSED EMA
        // +
        // current live market price
        //
        // This is exactly the missing piece that caused yesterday's
        // EMA direction to remain stale inside the current H1 candle.
        // =============================================================

        private static double CalculateLiveEma(
            double previousClosedEma,
            double livePrice,
            int period)
        {
            if (previousClosedEma <= 0 ||
                livePrice <= 0 ||
                period <= 0)
            {
                return previousClosedEma;
            }


            double alpha =
                2.0 /
                (period + 1.0);


            return
                previousClosedEma +
                alpha *
                (
                    livePrice -
                    previousClosedEma
                );
        }
        // Process-local live H1 observation. We record available quotes, not
        // reconstruct an MT5 bar. Each symbol is isolated and state is locked.
        private sealed class ObservedGoldH1
        {
            public long HourKey;
            public double Open;
            public double High;
            public double Low;
        }

        private static readonly object GoldH1Sync = new object();
        private static readonly System.Collections.Generic.Dictionary<string, ObservedGoldH1>
            GoldH1BySymbol = new System.Collections.Generic.Dictionary<string, ObservedGoldH1>(
                System.StringComparer.OrdinalIgnoreCase);

        private static ObservedGoldH1 ObserveGoldLiveH1(
            string symbol, MqlRates previousClosedH1, double livePrice)
        {
            long hour = System.DateTime.UtcNow.Ticks / System.TimeSpan.TicksPerHour;
            string key = symbol ?? "";
            lock (GoldH1Sync)
            {
                ObservedGoldH1 bar;
                if (!GoldH1BySymbol.TryGetValue(key, out bar) || bar.HourKey != hour)
                {
                    double open = previousClosedH1.close;
                    bar = new ObservedGoldH1
                    {
                        HourKey = hour,
                        Open = open,
                        High = Math.Max(open, livePrice),
                        Low = Math.Min(open, livePrice)
                    };
                    GoldH1BySymbol[key] = bar;
                }
                else
                {
                    bar.High = Math.Max(bar.High, livePrice);
                    bar.Low = Math.Min(bar.Low, livePrice);
                }
                return new ObservedGoldH1
                {
                    HourKey = bar.HourKey,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low
                };
            }
        }

        // Same directional wick-veto thresholds as the CASH/FX versions:
        // 35% of H1 range OR retreat of over 0.12 ATR from the extreme.
        private static bool PassesObservedGoldH1WickGuard(
            string symbol, ObservedGoldH1 bar, double atr,
            double bid, double ask, TradeAction action)
        {
            if (action != TradeAction.EnterBuy && action != TradeAction.EnterSell)
                return true;

            if (bar == null || atr <= 0 || bid <= 0 || ask < bid)
            {
                LogFile.Write($"[{symbol}] GOLD LIVE WICK WAIT | invalid candle/price");
                return false;
            }

            double price = (bid + ask) / 2.0;
            double high = Math.Max(bar.High, price);
            double low = Math.Min(bar.Low, price);
            double range = Math.Max(high - low, 0.0000001);
            double upperRatio = Math.Max(0, high - Math.Max(bar.Open, price)) / range;
            double lowerRatio = Math.Max(0, Math.Min(bar.Open, price) - low) / range;
            double recedeHighAtr = (high - price) / atr;
            double recedeLowAtr = (price - low) / atr;

            const double WickLimit = 0.35;
            const double RecedeLimit = 0.12;
            bool rejected = action == TradeAction.EnterBuy
                ? upperRatio >= WickLimit || recedeHighAtr > RecedeLimit
                : lowerRatio >= WickLimit || recedeLowAtr > RecedeLimit;

            if (rejected)
                LogFile.Write($"[{symbol}] GOLD LIVE WICK WAIT | action={action} | " +
                    $"upperWickRatio={upperRatio:F2} | lowerWickRatio={lowerRatio:F2} | " +
                    $"recedeHighATR={recedeHighAtr:F2} | recedeLowATR={recedeLowAtr:F2} | " +
                    $"open={bar.Open:F5} | high={high:F5} | low={low:F5} | price={price:F5}");

            return !rejected;
        }

    }
}
