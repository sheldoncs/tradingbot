using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.MetaTrader.Services;
using MetaTrader.Util;
using MtApi5;
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
            double ask, M15ConfirmationService _m15Service, out TradeType tradeType)
        {
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
            if (momentum < 1.0)
            {
                // 🔥 only block VERY weak markets
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
            
            return (TradeAction.DoNothing, EntryModel.Nothing);
        }
    }
}
