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
            double ask, M15ConfirmationService _m15Service, out TradeType tradeType, out bool isImpulse)
        {
            isImpulse = false;
            _m15Service.Update(symbol);
            tradeType = TradeType.None;
            // =========================
            // 1️⃣ Basic safety checks
            // =========================
            if (rates == null || rates.Length < 20)
                return (TradeAction.DoNothing, EntryModel.Nothing);
            
            if (atr <= 0)
                return (TradeAction.DoNothing, EntryModel.Nothing);

            // =========================
            // 2️⃣ Momentum gate (soft filter)
            // =========================
            // 🔥 allow impulse to bypass weak momentum
            bool allowImpulseBypass = false;

            var c0 = rates[^1];

            // quick live impulse check
            double liveMove = Math.Abs(bid - c0.open);
            double bodyAtr = liveMove / atr;

            if (bodyAtr > 0.8)   // early impulse threshold
            {
                allowImpulseBypass = true;
            }

            if (momentum < 1.0 && !allowImpulseBypass)
            {
                tradeType = TradeType.None;
                return (TradeAction.DoNothing, EntryModel.Nothing);
            }

            var state = MarketStateEngine.DetectState(
               symbol,
               rates,
               emaFast,
               emaSlow,
               atr
           );

            double currentPrice = rates[^1].close;

            // 🔥 Compression breakout (LIVE)
            if (state == MarketState.Compression)
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
                    tradeType = TradeType.Breakout;
                    double executionPrice =
                        action == TradeAction.EnterBuy ? ask : bid;
                    if (SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                action,
                                rates,
                                atr,
                                momentum,
                                executionPrice))
                    {
                        LogFile.Write("Blocked: selling into support");
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    // M15 (soft)
                    if (!_m15Service.IsTradeAllowed(symbol, action, EntryModel.CompressionBreakoutContinuation, momentum, out var reason))
                    {
                        if (momentum < 1.1)
                        {
                            LogFile.Write($"Blocked by M15: {reason}");
                            return (TradeAction.DoNothing, EntryModel.Nothing);
                        }

                        LogFile.Write("⚡ M15 overridden (strong breakout)");
                    }

                    return (action, EntryModel.CompressionBreakoutContinuation);
                }
            }

            
            if (state == MarketState.Trend)
            {
                // 🔥 OB/FVG tap-in
                if (OrderBlockFvgEngine.ShouldEnter(
                    symbol,
                    rates,
                    atr,
                    emaFast,
                    emaSlow,
                    out var obAction))
                {
                    tradeType = state == MarketState.EarlyBreakout ? TradeType.Breakout : TradeType.Continuation;
                    double executionPrice =
                        obAction == TradeAction.EnterBuy ? ask : bid;
                    if (SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                obAction,
                                rates,
                                atr,
                                momentum,
                                executionPrice))
                    {
                        LogFile.Write("Blocked: selling into support");
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    // M15 (soft)
                    if (!_m15Service.IsTradeAllowed(symbol, obAction, EntryModel.PullbackContinuation, momentum, out var reason))
                    {
                        if (momentum < 1.1)
                        {
                            LogFile.Write($"Blocked by M15: {reason}");
                            return (TradeAction.DoNothing, EntryModel.Nothing);
                        }

                        LogFile.Write("⚡ M15 overridden (strong breakout)");
                    }

                    return (obAction, EntryModel.PullbackContinuation);
                }
            }

        if (state == MarketState.Trend || state == MarketState.EarlyBreakout)
        {
            // 🔥 Strong breakout continuation
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
                    tradeType = state == MarketState.EarlyBreakout ? TradeType.Breakout : TradeType.Continuation;
                    double executionPrice =
                        breakoutAction == TradeAction.EnterBuy ? ask : bid;
                    if (SupportResistanceEngine.ShouldBlockTrade(
                                symbol,
                                breakoutAction,
                                rates,
                                atr,
                                momentum,
                                executionPrice))
                    {
                        LogFile.Write("Blocked: selling into support");
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    // M15 (soft)
                    if (!_m15Service.IsTradeAllowed(symbol, breakoutAction, EntryModel.CompressionBreakoutContinuation, momentum, out var reason))
                    {
                        if (momentum < 1.1)
                        {
                            LogFile.Write($"Blocked by M15: {reason}");
                            return (TradeAction.DoNothing, EntryModel.Nothing);
                        }

                        LogFile.Write("⚡ M15 overridden (strong breakout)");
                    }

                    return (breakoutAction, EntryModel.BreakoutContinuation);
                }
            }

            // =============================
            // ⚡ IMPULSE ENTRY (NEW)
            // =============================
            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];
            // 🔥 Compute recent structure (reuse your logic if you already have this)
            double recentHigh = rates.Skip(rates.Length - 10).Max(r => r.high);
            double recentLow = rates.Skip(rates.Length - 10).Min(r => r.low);

            if (ImpulseEntryEngine.TryEnter(
                    symbol,
                    c0,
                    atr,
                    ema9,
                    ema21,
                    recentHigh,
                    recentLow, bid, ask,
                    out var impulseAction))
            {
                tradeType = TradeType.Breakout;

                double executionPrice =
                    impulseAction == TradeAction.EnterBuy ? ask : bid;

                if (SupportResistanceEngine.ShouldBlockTrade(
                        symbol,
                        impulseAction,
                        rates,
                        atr,
                        momentum,
                        executionPrice))
                {
                    LogFile.Write("Blocked: impulse into SR");
                    return (TradeAction.DoNothing, EntryModel.Nothing);
                }

                // 🔥 M15 softer for impulse
                if (!_m15Service.IsTradeAllowed(
                        symbol,
                        impulseAction,
                        EntryModel.BreakoutContinuation,
                        momentum,
                        out var reason))
                {
                    if (momentum < 1.2) // slightly higher threshold for impulse override
                    {
                        LogFile.Write($"Impulse blocked by M15: {reason}");
                        return (TradeAction.DoNothing, EntryModel.Nothing);
                    }

                    LogFile.Write("⚡ M15 overridden (impulse)");
                }

                isImpulse = true;
                return (impulseAction, EntryModel.BreakoutContinuation);
            }

            return (TradeAction.DoNothing, EntryModel.Nothing);
        }
    }
}
