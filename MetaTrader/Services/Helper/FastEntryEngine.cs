using MetaTrader.Data.Entities;
using MetaTrader.Enums;
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
            double ask, M15ConfirmationService _m15Service)
        {
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
                return (TradeAction.DoNothing, EntryModel.Nothing);
            }

            double currentPrice = rates[^1].close;
            // 🔥 Compression breakout (LIVE)
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

            // 🔥 OB/FVG tap-in
            if (OrderBlockFvgEngine.ShouldEnter(
                symbol,
                rates,
                atr,
                emaFast,
                emaSlow,
                out var obAction))
            {
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
                out var breakoutAction))
            {

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

            return (TradeAction.DoNothing, EntryModel.Nothing);
        }
    }
}
