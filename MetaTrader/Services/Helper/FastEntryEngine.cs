using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.MetaTrader.Services;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;
using static MetaTrader.Services.HybridMomentumEngine;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Services.Helper
{
    public static class FastEntryEngine
    {
        // GOLD S/R ENTRY EXPERIMENT (DEMO / BACKTEST ONLY)
        // true: calculate and log all S/R decisions, but do not veto Gold entries.
        // false: preserve the original production S/R behavior.
        // This does not bypass follow-through, M15, wick/anti-chase, SL or risk logic.
        private const bool GoldSrObservationOnly = true;

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

                LogFile.Write(
                    $"[{symbol}] <<< IMPULSE ENGINE RESULT | " +
                    $"detected={impulseDetected} | " +
                    $"action={impulseAction} | " +
                    $"goldBreakdownTriggered={goldBreakdownTriggered}"
                );



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


                    bool confirmedBreakout =
                        impulseAction == TradeAction.EnterBuy
                            ? ask > recentHigh
                            : bid < recentLow;

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
                    // Exception:
                    //     An exceptionally strong H1 impulse may override
                    //     missing/opposite M15 confirmation at momentum >= 1.20.
                    // =====================================================

                    var m15 =
                        _m15Service.GetSnapshot(
                            symbol
                        );


                    // Gold H1 alternative: the impulse has already passed the
                    // impulse detector, follow-through, opposite-H1 and HARD SR
                    // checks above. Do not add another 1.20 momentum requirement
                    // merely because the last CLOSED M15 has not caught up.
                    // Other symbols preserve their previous M15 behavior.
                    bool goldH1ImpulseAlternative =
                        IsGold(symbol) &&
                        (impulseAction == TradeAction.EnterBuy ||
                         impulseAction == TradeAction.EnterSell) &&
                        (impulseAction == TradeAction.EnterBuy
                            ? ema9 > ema21 && ema9 > ema9Previous && liveMid > ema9
                            : ema9 < ema21 && ema9 < ema9Previous && liveMid < ema9) &&
                        (impulseAction == TradeAction.EnterBuy
                            ? Math.Max(0.0, liveMid - liveCandle.open)
                            : Math.Max(0.0, liveCandle.open - liveMid)) / atr >= 0.55 &&
                        Math.Abs(momentum) >= 0.30 &&
                        emaDistanceAtr <= 1.25 &&
                        (confirmedBreakout || goldBreakdownTriggered);

                    bool allowStrongImpulseThroughM15 =
                        goldH1ImpulseAlternative || momentum >= impulseOverrideThreshold;

                    if (goldH1ImpulseAlternative)
                        LogFile.Write($"[{symbol}] GOLD H1 BREAKOUT M15 ALTERNATIVE | " +
                            $"action={impulseAction} | breakout={confirmedBreakout} | " +
                            $"armedBreak={goldBreakdownTriggered} | bodyATR={bodyAtr:F2} | " +
                            $"momentum={momentum:F2} | emaDistATR={emaDistanceAtr:F2} | " +
                            "upstreamSR=PASSED | confirmation=H1");


                    if (m15 == null)
                    {
                        if (!allowStrongImpulseThroughM15)
                        {
                            LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | reason=M15_NO_SNAPSHOT | action={impulseAction} | momentum={momentum:F2}");
                            LogFile.Write(
                                $"[{symbol}] ❌ IMPULSE BLOCKED - NO CLOSED M15 CONFIRMATION | " +
                                $"action={impulseAction} | momentum={momentum:F2}"
                            );

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
                // 12. TREND CONTINUATION ENTRY
                //
                // PURPOSE:
                //
                // Catch a fresh continuation after:
                //
                //     established trend
                //          ↓
                //     pullback toward EMA9 / EMA21
                //          ↓
                //     rejection
                //          ↓
                //     continuation break
                //
                // IMPORTANT:
                //
                // This is NOT an impulse.
                //
                // Therefore:
                //
                // - do NOT set impulseDetected = true
                // - do NOT use the impulse opposite-candle filter
                //   because the opposite pullback candle is EXPECTED here
                // - do NOT send it through the global 0.60 momentum gate
                //
                // TrendContinuationEntryEngine has its own
                // asset-specific momentum requirements.
                //
                // Still requires:
                //
                // - Support / Resistance
                // - CLOSED M15 confirmation
                // =========================================================

                if (!impulseDetected)
                {
                    LogFile.Write(
                        $"[{symbol}] >>> CALLING TREND CONTINUATION ENGINE | " +
                        $"liveOpen={liveCandle.open:F5} | " +
                        $"price={liveMid:F5} | " +
                        $"ema9={ema9:F5} | " +
                        $"ema9Prev={ema9Previous:F5} | " +
                        $"ema21={ema21:F5} | " +
                        $"momentum={momentum:F2}"
                    );


                    bool continuationDetected =
                        TrendContinuationEntryEngine.TryEnter(
                            symbol,
                            liveCandle,
                            previousCandle,
                            rates,
                            atr,
                            ema9,
                            ema9Previous,
                            ema21,
                            momentum,
                            bid,
                            ask,
                            out TradeAction continuationAction
                        );


                    LogFile.Write(
                        $"[{symbol}] <<< TREND CONTINUATION ENGINE RESULT | " +
                        $"detected={continuationDetected} | " +
                        $"action={continuationAction}"
                    );


                    if (continuationDetected)
                    {
                        tradeType =
                            TradeType.Continuation;


                        double executionPrice =
                            continuationAction ==
                                TradeAction.EnterBuy
                                    ? ask
                                    : bid;


                        double continuationEmaDistanceAtr =
                            atr > 0.0
                                ? Math.Abs(
                                    liveMid - ema9
                                ) / atr
                                : 0.0;


                        // =================================================
                        // 12A. SUPPORT / RESISTANCE
                        //
                        // No strong-momentum override here.
                        //
                        // Continuation entries should not punch directly
                        // through support / resistance simply because
                        // momentum happens to be high.
                        // =================================================

                        bool continuationSrBlocked =
                            SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                continuationAction,
                                rates,
                                atr,
                                momentum,
                                executionPrice,
                                ema9,
                                out SupportResistanceEngine.SrEntryDiagnostics continuationSrDiagnostics,
                                recentHigh,
                                recentLow,
                                isImpulseEntry: false,
                                isArmedBreakdownTrigger: goldBreakdownTriggered
                            );


                        if (ObserveGoldSrBlock(symbol, continuationSrBlocked, continuationAction, "TREND_CONTINUATION"))
                        {
                            LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | path=TREND_CONTINUATION | reason=SR | action={continuationAction} | momentum={momentum:F2}");
                            LogFile.Write(
                                $"[{symbol}] ❌ TREND CONTINUATION BLOCKED BY SR | " +
                                $"action={continuationAction} | " +
                                $"price={executionPrice:F5} | " +
                                $"momentum={momentum:F2} | " +
                                $"emaDist={continuationEmaDistanceAtr:F2}"
                            );


                            tradeType =
                                TradeType.None;


                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        // =================================================
                        // 12B. CLOSED M15 CONFIRMATION
                        //
                        // BUY  -> latest CLOSED M15 must be bullish
                        // SELL -> latest CLOSED M15 must be bearish
                        //
                        // No M15 override for this continuation path.
                        // =================================================

                        var continuationM15 =
                            _m15Service.GetSnapshot(
                                symbol
                            );


                        if (continuationM15 == null && IsGold(symbol))
                        {
                            // A missing M15 snapshot is not affirmative evidence
                            // against the independently qualified Gold H1 signal.
                            // SR has already passed. Do not manufacture M15 candles.
                            LogFile.Write($"[{symbol}] GOLD CONTINUATION M15 DATA UNAVAILABLE | " +
                                $"action={continuationAction} | upstreamSR=PASSED | " +
                                "confirmation=QUALIFIED_H1_CONTINUATION");
                        }
                        else if (continuationM15 == null)
                        {
                            LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | path=TREND_CONTINUATION | reason=M15_NO_SNAPSHOT | action={continuationAction}");
                            LogFile.Write(
                                $"[{symbol}] ❌ TREND CONTINUATION BLOCKED - " +
                                $"NO CLOSED M15 SNAPSHOT | " +
                                $"action={continuationAction}"
                            );


                            tradeType =
                                TradeType.None;


                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        bool continuationM15Buy =
                            continuationAction ==
                                TradeAction.EnterBuy &&
                            (continuationM15?.Bullish ?? false);


                        bool continuationM15Sell =
                            continuationAction ==
                                TradeAction.EnterSell &&
                            (continuationM15?.Bearish ?? false);


                        bool continuationM15Confirms =
                            continuationM15Buy ||
                            continuationM15Sell;


                        // Gold H1 early-continuation exception: do not require an M15
                        // signal to catch up when an already-detected continuation
                        // is directional, expanding and still within its EMA range.
                        // An explicitly opposite M15 signal is NOT overridden.
                        // All upstream structure / SR checks remain mandatory.
                        double continuationDirectionalBodyAtr = atr > 0.0
                            ? (continuationAction == TradeAction.EnterSell
                                ? Math.Max(0.0, liveCandle.open - liveMid)
                                : Math.Max(0.0, liveMid - liveCandle.open)) / atr
                            : 0.0;

                        bool continuationH1Aligned =
                            continuationAction == TradeAction.EnterSell
                                ? ema9 < ema21 && ema9 < ema9Previous && liveMid < ema9
                                : ema9 > ema21 && ema9 > ema9Previous && liveMid > ema9;

                        bool continuationM15Opposes =
                            continuationAction == TradeAction.EnterSell
                                ? (continuationM15?.Bullish ?? false)
                                : (continuationM15?.Bearish ?? false);

                        // The continuation engine already qualified the H1 reset,
                        // direction, momentum, candle body and entry distance;
                        // SR has passed too. Do not repeat stronger thresholds
                        // here or require a lagging closed M15 candle to agree.
                        bool allowEarlyGoldH1Continuation =
                            IsGold(symbol) &&
                            !continuationM15Confirms &&
                            continuationH1Aligned;

                        if (allowEarlyGoldH1Continuation)
                        {
                            LogFile.Write(
                                $"[{symbol}] GOLD EARLY H1 CONTINUATION M15 EXCEPTION | " +
                                $"action={continuationAction} | momentum={momentum:F3} | " +
                                $"bodyATR={continuationDirectionalBodyAtr:F3} | " +
                                $"emaDistanceATR={continuationEmaDistanceAtr:F3} | " +
                                $"M15Bullish={continuationM15?.Bullish} | " +
                                $"M15Bearish={continuationM15?.Bearish} | " +
                                $"M15CandleTime={continuationM15?.CandleTime} | " +
                                "reason=H1 continuation already detected and SR passed");
                        }

                        if (!continuationM15Confirms && !allowEarlyGoldH1Continuation)
                        {
                            LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | path=TREND_CONTINUATION | reason=M15_NOT_CONFIRMED | action={continuationAction} | bullish={continuationM15.Bullish} | bearish={continuationM15.Bearish}");
                            LogFile.Write(
                                $"[{symbol}] ❌ TREND CONTINUATION BLOCKED - " +
                                $"CLOSED M15 DOES NOT CONFIRM | " +
                                $"action={continuationAction} | " +
                                $"M15Bullish={continuationM15.Bullish} | " +
                                $"M15Bearish={continuationM15.Bearish} | " +
                                $"M15CandleTime={continuationM15.CandleTime} | " +
                                $"momentum={momentum:F2}"
                            );


                            tradeType =
                                TradeType.None;


                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        if (continuationM15Confirms) LogFile.Write(
                            $"[{symbol}] ✅ CLOSED M15 CONFIRMS TREND CONTINUATION | " +
                            $"action={continuationAction} | " +
                            $"M15Bullish={continuationM15.Bullish} | " +
                            $"M15Bearish={continuationM15.Bearish} | " +
                            $"M15CandleTime={continuationM15.CandleTime} | " +
                            $"momentum={momentum:F2}"
                        );


                        // =================================================
                        // 12C. CONTINUATION APPROVED
                        // =================================================

                        isImpulse =
                            false;


                        // Directional current-H1 wick protection (same thresholds as CASH/FX).
                        if (!PassesObservedGoldH1WickGuard(symbol, observedGoldH1, atr, bid, ask, continuationAction))
                        {
                            tradeType = TradeType.None;
                            isImpulse = false;
                            return (TradeAction.DoNothing, EntryModel.Nothing);
                        }

                        ApproveEntryDiagnostics(continuationSrDiagnostics);

                        LogFile.Write($"[{symbol}] GOLD FASTENTRY GATE DIAGNOSTIC | path=TREND_CONTINUATION | reason=ALL_GATES_PASSED | action={continuationAction} | momentum={momentum:F2}");
                        LogFile.Write(
                            $"[{symbol}] ✅ TREND CONTINUATION ENTRY APPROVED | " +
                            $"action={continuationAction} | " +
                            $"price={executionPrice:F5} | " +
                            $"momentum={momentum:F2} | " +
                            $"ema9={ema9:F5} | " +
                            $"ema21={ema21:F5} | " +
                            $"emaDist={continuationEmaDistanceAtr:F2} | " +
                            $"srAhead={continuationSrDiagnostics.WallAhead} | " +
                            $"srWall={continuationSrDiagnostics.Wall:F5} | " +
                            $"srClearanceATR={continuationSrDiagnostics.ClearanceAtr:F2}"
                        );


                        return (
                            continuationAction,
                            EntryModel.PullbackContinuation
                        );
                    }
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
                // 15. COMPRESSION BREAKOUT
                // =========================================================

                if (state ==
                    MarketState.Compression)
                {
                    if (CompressionBreakoutEngine.ShouldEnterLive(
                            symbol,
                            rates,
                            atr,
                            liveEmaFast,
                            liveEmaSlow,
                            momentum,
                            bid,
                            ask,
                            out var action))
                    {
                        tradeType =
                            TradeType.Breakout;


                        double executionPrice =
                            action ==
                                TradeAction.EnterBuy
                                    ? ask
                                    : bid;


                        if (ObserveGoldSrBlock(symbol, SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                action,
                                rates,
                                atr,
                                momentum,
                                executionPrice,
                                ema9,
                                out SupportResistanceEngine.SrEntryDiagnostics compressionSrDiagnostics), action, "COMPRESSION"))
                        {
                            LogFile.Write(
                                $"[{symbol}] Compression blocked by SR"
                            );


                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        // ================================================
                        // M15 SOFT CONFIRMATION
                        // ================================================

                        if (!_m15Service.IsTradeAllowed(
                                symbol,
                                action,
                                EntryModel.CompressionBreakoutContinuation,
                                momentum,
                                out var reason))
                        {
                            if (momentum < 1.1)
                            {
                                LogFile.Write(
                                    $"[{symbol}] Compression blocked by M15: " +
                                    $"{reason}"
                                );


                                return (
                                    TradeAction.DoNothing,
                                    EntryModel.Nothing
                                );
                            }


                            LogFile.Write(
                                $"[{symbol}] ⚡ M15 overridden " +
                                $"for strong compression breakout"
                            );
                        }


                        // Directional current-H1 wick protection (same thresholds as CASH/FX).
                        if (!PassesObservedGoldH1WickGuard(symbol, observedGoldH1, atr, bid, ask, action))
                        {
                            tradeType = TradeType.None;
                            isImpulse = false;
                            return (TradeAction.DoNothing, EntryModel.Nothing);
                        }

                        ApproveEntryDiagnostics(compressionSrDiagnostics);

                        return (
                            action,
                            EntryModel.CompressionBreakoutContinuation
                        );
                    }
                }


                // =========================================================
                // 16. ORDER BLOCK / FVG
                // =========================================================

                if (state ==
                    MarketState.Trend)
                {
                    if (OrderBlockFvgEngine.ShouldEnter(
                            symbol,
                            rates,
                            atr,
                            liveEmaFast,
                            liveEmaSlow,
                            out var obAction))
                    {
                        tradeType =
                            TradeType.Continuation;


                        double executionPrice =
                            obAction ==
                                TradeAction.EnterBuy
                                    ? ask
                                    : bid;


                        if (ObserveGoldSrBlock(symbol, SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                obAction,
                                rates,
                                atr,
                                momentum,
                                executionPrice,
                                ema9,
                                out SupportResistanceEngine.SrEntryDiagnostics obSrDiagnostics), obAction, "OB_FVG"))
                        {
                            LogFile.Write(
                                $"[{symbol}] OB/FVG blocked by SR"
                            );


                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        // ================================================
                        // M15 SOFT CONFIRMATION
                        // ================================================

                        if (!_m15Service.IsTradeAllowed(
                                symbol,
                                obAction,
                                EntryModel.PullbackContinuation,
                                momentum,
                                out var reason))
                        {
                            if (momentum < 1.1)
                            {
                                LogFile.Write(
                                    $"[{symbol}] OB/FVG blocked by M15: " +
                                    $"{reason}"
                                );


                                return (
                                    TradeAction.DoNothing,
                                    EntryModel.Nothing
                                );
                            }


                            LogFile.Write(
                                $"[{symbol}] ⚡ M15 overridden " +
                                $"for strong OB/FVG continuation"
                            );
                        }


                        // Directional current-H1 wick protection (same thresholds as CASH/FX).
                        if (!PassesObservedGoldH1WickGuard(symbol, observedGoldH1, atr, bid, ask, obAction))
                        {
                            tradeType = TradeType.None;
                            isImpulse = false;
                            return (TradeAction.DoNothing, EntryModel.Nothing);
                        }

                        ApproveEntryDiagnostics(obSrDiagnostics);

                        return (
                            obAction,
                            EntryModel.PullbackContinuation
                        );
                    }
                }


                // =========================================================
                // 17. BREAKOUT CONTINUATION
                //
                // Impulse = early expansion
                //
                // BreakoutContinuation = later confirmation
                // =========================================================

                if (state ==
                        MarketState.Trend ||
                    state ==
                        MarketState.EarlyBreakout)
                {
                    if (BreakoutContinuationEngine.ShouldEnterLive(
                            symbol,
                            rates,
                            atr,
                            liveEmaFast,
                            liveEmaSlow,
                            ema9Previous,
                            momentum,
                            bid,
                            ask,
                            state,
                            out var breakoutAction))
                    {



                        tradeType =
                            state ==
                                MarketState.EarlyBreakout
                                    ? TradeType.Breakout
                                    : TradeType.Continuation;


                        double executionPrice =
                            breakoutAction ==
                                TradeAction.EnterBuy
                                    ? ask
                                    : bid;


                        if (ObserveGoldSrBlock(symbol, SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                breakoutAction,
                                rates,
                                atr,
                                momentum,
                                executionPrice,
                                ema9,
                                out SupportResistanceEngine.SrEntryDiagnostics breakoutSrDiagnostics,
                                recentHigh,
                                recentLow), breakoutAction, "BREAKOUT"))
                        {
                            LogFile.Write(
                                $"[{symbol}] BreakoutContinuation " +
                                $"blocked by SR"
                            );


                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        // ================================================
                        // M15 SOFT CONFIRMATION
                        // ================================================

                        if (!_m15Service.IsTradeAllowed(
                                symbol,
                                breakoutAction,
                                EntryModel.CompressionBreakoutContinuation,
                                momentum,
                                out var reason))
                        {
                            if (momentum < 1.1)
                            {
                                LogFile.Write(
                                    $"[{symbol}] BreakoutContinuation " +
                                    $"blocked by M15: {reason}"
                                );


                                return (
                                    TradeAction.DoNothing,
                                    EntryModel.Nothing
                                );
                            }


                            LogFile.Write(
                                $"[{symbol}] ⚡ M15 overridden " +
                                $"for strong breakout"
                            );
                        }


                        // Directional current-H1 wick protection (same thresholds as CASH/FX).
                        if (!PassesObservedGoldH1WickGuard(symbol, observedGoldH1, atr, bid, ask, breakoutAction))
                        {
                            tradeType = TradeType.None;
                            isImpulse = false;
                            return (TradeAction.DoNothing, EntryModel.Nothing);
                        }

                        ApproveEntryDiagnostics(breakoutSrDiagnostics);

                        return (
                            breakoutAction,
                            EntryModel.BreakoutContinuation
                        );
                    }
                }


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
