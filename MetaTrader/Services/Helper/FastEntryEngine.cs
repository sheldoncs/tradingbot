using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.MetaTrader.Services;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;
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
                // ==================================================
                // 1️⃣ BASIC SAFETY CHECKS
                // ==================================================

                if (rates == null || rates.Length < 20)
                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );

                if (atr <= 0)
                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );

                if (emaFast == null ||
                    emaFast.Length < 2 ||
                    emaSlow == null ||
                    emaSlow.Length == 0)
                {
                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );
                }

                _m15Service.Update(symbol);


                // ==================================================
                // 2️⃣ PREVIOUS CLOSED CANDLE
                // ==================================================

                var previousCandle = rates[^1];


                // ==================================================
                // 3️⃣ LIVE MARKET PRICE
                // ==================================================

                double liveMid =
                    (bid + ask) / 2.0;


                // ==================================================
                // 4️⃣ SYNTHETIC LIVE CANDLE
                //
                // rates[^1] is the previous CLOSED candle.
                //
                // Using previous close as current H1 open.
                // ==================================================

                double liveOpen =
                    previousCandle.close;

                var liveCandle =
                    new MqlRates
                    {
                        open = liveOpen,
                        high = Math.Max(
                            liveOpen,
                            liveMid
                        ),
                        low = Math.Min(
                            liveOpen,
                            liveMid
                        ),
                        close = liveMid
                    };


                // ==================================================
                // 5️⃣ EMA VALUES
                // ==================================================

                double ema9 =
                    emaFast[^1];

                double ema9Previous =
                    emaFast[^2];

                double ema21 =
                    emaSlow[^1];


                // ==================================================
                // 6️⃣ RECENT CLOSED STRUCTURE
                //
                // Do NOT include synthetic live candle.
                // ==================================================

                double recentHigh =
                    rates
                        .Skip(
                            Math.Max(
                                0,
                                rates.Length - 10
                            )
                        )
                        .Max(r => r.high);

                double recentLow =
                    rates
                        .Skip(
                            Math.Max(
                                0,
                                rates.Length - 10
                            )
                        )
                        .Min(r => r.low);


                // ==================================================
                // 🚀 7️⃣ IMPULSE ENTRY — FIRST PRIORITY
                //
                // IMPORTANT:
                //
                // Impulse is evaluated BEFORE:
                // - global momentum gate
                // - Compression
                // - OB/FVG
                // - BreakoutContinuation
                //
                // This gives early expansion a chance to enter
                // before slower confirmation engines take over.
                // ==================================================

                LogFile.Write(
                    $"[{symbol}] >>> CALLING IMPULSE ENGINE | " +
                    $"liveOpen={liveCandle.open:F5} | " +
                    $"bid={bid:F5} | " +
                    $"ask={ask:F5} | " +
                    $"atr={atr:F5} | " +
                    $"ema9={ema9:F5} | " +
                    $"ema9Prev={ema9Previous:F5} | " +
                    $"ema21={ema21:F5} | " +
                    $"recentHigh={recentHigh:F5} | " +
                    $"recentLow={recentLow:F5} | " +
                    $"momentum={momentum:F2}"
                );

                bool impulseDetected =
                    ImpulseEntryEngine.TryEnter(
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

                if (impulseDetected)
                {
                    tradeType =
                        TradeType.Breakout;

                    double executionPrice =
                        impulseAction ==
                            TradeAction.EnterBuy
                                ? ask
                                : bid;


                    // ==================================================
                    // SUPPORT / RESISTANCE CHECK
                    // ==================================================

                    bool srBlocked =
                    SupportResistanceEngine.ShouldBlockTrade(
                        symbol,
                        impulseAction,
                        rates,
                        atr,
                        momentum,
                        executionPrice
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


                    // ==================================================
                    // M15 SOFT CONFIRMATION
                    //
                    // Keep M15 protection for now.
                    //
                    // Strong momentum can override disagreement.
                    // ==================================================

                    // ==================================================
                    // 🔥 M15 IMPULSE CONFIRMATION
                    //
                    // For an early impulse, M15 does NOT need to
                    // positively confirm the breakout.
                    //
                    // We only block if M15 is STRONGLY OPPOSING
                    // the impulse direction.
                    // ==================================================

                    var m15 =
                        _m15Service.GetSnapshot(symbol);

                    if (m15 == null)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⚠️ No M15 snapshot for impulse - allowing impulse"
                        );
                    }
                    else
                    {
                        bool m15StronglyOpposesBuy =
                            impulseAction == TradeAction.EnterBuy &&
                            m15.Bearish;

                        bool m15StronglyOpposesSell =
                            impulseAction == TradeAction.EnterSell &&
                            m15.Bullish;

                        bool m15StrongOpposition =
                            m15StronglyOpposesBuy ||
                            m15StronglyOpposesSell;

                        if (m15StrongOpposition)
                        {
                            LogFile.Write(
                                $"[{symbol}] ❌ IMPULSE BLOCKED BY STRONG M15 OPPOSITION | " +
                                $"action={impulseAction} | " +
                                $"M15Bullish={m15.Bullish} | " +
                                $"M15Bearish={m15.Bearish} | " +
                                $"momentum={momentum:F2}"
                            );

                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }

                        LogFile.Write(
                            $"[{symbol}] ✅ M15 DOES NOT OPPOSE IMPULSE | " +
                            $"action={impulseAction} | " +
                            $"M15Bullish={m15.Bullish} | " +
                            $"M15Bearish={m15.Bearish} | " +
                            $"momentum={momentum:F2}"
                        );
                    }


                    // ==================================================
                    // IMPULSE APPROVED
                    // ==================================================

                    isImpulse = true;

                    LogFile.Write(
                        $"[{symbol}] ✅ IMPULSE ENTRY APPROVED | " +
                        $"action={impulseAction} | " +
                        $"price={executionPrice:F5}"
                    );

                    return (
                        impulseAction,
                        EntryModel.BreakoutContinuation
                    );
                }


                // ==================================================
                // 8️⃣ MOMENTUM GATE
                //
                // IMPORTANT:
                //
                // This gate now applies ONLY AFTER Impulse had
                // its chance.
                //
                // Therefore low momentum can stop slower engines
                // without preventing a valid transition impulse.
                // ==================================================

                if (momentum < 1.0)
                {
                    LogFile.Write(
                        $"[{symbol}] Normal entry engines blocked | " +
                        $"momentum={momentum:F2} < 1.00 | " +
                        $"Impulse already evaluated=False"
                    );

                    tradeType =
                        TradeType.None;

                    return (
                        TradeAction.DoNothing,
                        EntryModel.Nothing
                    );
                }


                // ==================================================
                // 9️⃣ MARKET STATE
                // ==================================================

                var state =
                    MarketStateEngine.DetectState(
                        symbol,
                        rates,
                        emaFast,
                        emaSlow,
                        atr
                    );


                // ==================================================
                // 🔥 10️⃣ COMPRESSION BREAKOUT
                // ==================================================

                if (state ==
                    MarketState.Compression)
                {
                    if (CompressionBreakoutEngine.ShouldEnterLive(
                            symbol,
                            rates,
                            atr,
                            emaFast,
                            emaSlow,
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
                                executionPrice))
                        {
                            LogFile.Write(
                                $"[{symbol}] Compression blocked by SR"
                            );

                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        // ===============================
                        // M15 SOFT CONFIRMATION
                        // ===============================

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


                // ==================================================
                // 🔥 11️⃣ ORDER BLOCK / FVG
                // ==================================================

                if (state ==
                    MarketState.Trend)
                {
                    if (OrderBlockFvgEngine.ShouldEnter(
                            symbol,
                            rates,
                            atr,
                            emaFast,
                            emaSlow,
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
                                executionPrice))
                        {
                            LogFile.Write(
                                $"[{symbol}] OB/FVG blocked by SR"
                            );

                            return (
                                TradeAction.DoNothing,
                                EntryModel.Nothing
                            );
                        }


                        // ===============================
                        // M15 SOFT CONFIRMATION
                        // ===============================

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


                // ==================================================
                // 🔥 12️⃣ BREAKOUT CONTINUATION
                //
                // This is intentionally AFTER Impulse.
                //
                // Impulse = early expansion
                // BreakoutContinuation = later confirmation
                // ==================================================

                if (state ==
                        MarketState.Trend ||
                    state ==
                        MarketState.EarlyBreakout)
                {
                    if (BreakoutContinuationEngine.ShouldEnterLive(
                            symbol,
                            rates,
                            atr,
                            emaFast,
                            emaSlow,
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
                                executionPrice))
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


                        // ===============================
                        // M15 SOFT CONFIRMATION
                        // ===============================

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


                // ==================================================
                // NOTHING QUALIFIED
                // ==================================================

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

                isImpulse = false;
                tradeType = TradeType.None;

                return (
                    TradeAction.DoNothing,
                    EntryModel.Nothing
                );
            }
        }
    }
}
