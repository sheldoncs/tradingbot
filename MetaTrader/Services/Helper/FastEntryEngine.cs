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
        public static (TradeAction, EntryModel) Evaluate(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double momentum,
            double bid,
            double ask,
            M15ConfirmationService _m15Service,
            out TradeType tradeType,
            out bool isImpulse)
        {
            isImpulse = false;
            tradeType = TradeType.None;

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


                bool impulseDetected =
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
                        out var impulseAction
                    );

                LogFile.Write(
                    $"[{symbol}] <<< IMPULSE ENGINE RESULT | " +
                    $"detected={impulseDetected} | " +
                    $"action={impulseAction}"
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
                    if (!PassesStrongOppositeCandleScenarioFilter(
                            symbol,
                            previousCandle,
                            atr,
                            impulseAction))
                    {
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


                    // =====================================================
                    // 11A. SUPPORT / RESISTANCE
                    //
                    // Strong impulse can override SR.
                    // =====================================================

                    bool srBlocked =
                        SupportResistanceEngine.ShouldBlockTrade(
                            symbol,
                            impulseAction,
                            rates,
                            atr,
                            momentum,
                            executionPrice,
                            ema9,
                            recentHigh,
                            recentLow
                        );


                    if (srBlocked)
                    {
                        bool allowStrongImpulseThroughSr =
                            momentum >= 1.20;


                        if (!allowStrongImpulseThroughSr)
                        {
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


                    bool allowStrongImpulseThroughM15 =
                        momentum >= 1.20;


                    if (m15 == null)
                    {
                        if (!allowStrongImpulseThroughM15)
                        {
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


                    LogFile.Write(
                        $"[{symbol}] ✅ IMPULSE ENTRY APPROVED | " +
                        $"action={impulseAction} | " +
                        $"price={executionPrice:F5} | " +
                        $"ema9={ema9:F5} | " +
                        $"ema21={ema21:F5}"
                    );


                    return (
                        impulseAction,
                        EntryModel.ImpulseBreakout
                    );
                }


                // =========================================================
                // 12. GLOBAL MOMENTUM GATE
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
                // 13. MARKET STATE
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
                // 14. COMPRESSION BREAKOUT
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


                        if (SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                action,
                                rates,
                                atr,
                                momentum,
                                executionPrice,
                                ema9))
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


                        return (
                            action,
                            EntryModel.CompressionBreakoutContinuation
                        );
                    }
                }


                // =========================================================
                // 15. ORDER BLOCK / FVG
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


                        if (SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                obAction,
                                rates,
                                atr,
                                momentum,
                                executionPrice,
                                ema9))
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


                        return (
                            obAction,
                            EntryModel.PullbackContinuation
                        );
                    }
                }


                // =========================================================
                // 16. BREAKOUT CONTINUATION
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


                        if (SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                breakoutAction,
                                rates,
                                atr,
                                momentum,
                                executionPrice,
                                ema9,
                                recentHigh,
                                recentLow))
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


                        return (
                            breakoutAction,
                            EntryModel.BreakoutContinuation
                        );
                    }
                }


                // =========================================================
                // 17. NOTHING QUALIFIED
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
    }
}
