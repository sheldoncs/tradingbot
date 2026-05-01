using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.Helper;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class ProfitProtectionEngine
    {
        private class Profile
        {
            public double ActivationAtrFactor;
            public double Stage1AtrFactor;
            public double Stage2AtrFactor;
            public double Stage3AtrFactor;
        }

        // --------------------------------
        // 🔥 PROFILE PER ASSET
        // --------------------------------
        private static Profile GetProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.2,
                    Stage1AtrFactor = 1.0,
                    Stage2AtrFactor = 1.8,
                    Stage3AtrFactor = 2.8
                };
            }

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.4,
                    Stage1AtrFactor = 1.2,
                    Stage2AtrFactor = 2.0,
                    Stage3AtrFactor = 3.2
                };
            }

            if (symbol.Contains("USD") &&
                (symbol.Contains("ETH") || symbol.Contains("BTC") || symbol.Contains("SOL") ||
                 symbol.Contains("AVAX") || symbol.Contains("BNB") || symbol.Contains("XRP")))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.6,
                    Stage1AtrFactor = 1.3,
                    Stage2AtrFactor = 2.3,
                    Stage3AtrFactor = 3.5
                };
            }

            // FX default
            return new Profile
            {
                ActivationAtrFactor = 1.0,
                Stage1AtrFactor = 0.8,
                Stage2AtrFactor = 1.5,
                Stage3AtrFactor = 2.2
            };
        }

        public static bool ShouldCloseTrade(
            TrackedTrade trade,
            string symbol,
            double atr, MqlRates[] rates1Hr, double[] emaFast)
        {
            if (trade == null || atr <= 0)
                return false;

            // --------------------------------
            // 🔥 TRACK MAX PROFIT
            // --------------------------------
            trade.MaxProfitUsd = Math.Max(trade.MaxProfitUsd, trade.ProfitUsd);

            // --------------------------------
            // 🔥 HARD GUARD — NEVER CLOSE LOSING TRADES
            // --------------------------------
            if (trade.ProfitUsd <= 0)
                return false;

            var profile = GetProfile(symbol);

            // --------------------------------
            // 🔥 ACTIVATION (ATR-BASED)
            // --------------------------------
            double activationLevel = atr * profile.ActivationAtrFactor;

            if (trade.MaxProfitUsd < activationLevel)
                return false;

            // --------------------------------
            // 🔥 STAGE 1
            // --------------------------------
            if (trade.MaxProfitUsd < activationLevel * 2)
            {
                double buffer = atr * profile.Stage1AtrFactor;

                bool stillHealthy = ContinuationValidator.IsHealthyContinuation(symbol,
                    trade.Side == TradeSide.Buy ? TradeAction.EnterBuy : TradeAction.EnterSell,
                    rates1Hr,
                    emaFast,
                    atr);

                if (!stillHealthy && trade.ProfitUsd <= trade.MaxProfitUsd - buffer)
                {
                    LogFile.Write($"[{symbol}] Protection exit (continuation failed)");
                    return true;
                }
            }

            // --------------------------------
            // 🔥 STAGE 2
            // --------------------------------
            if (trade.MaxProfitUsd >= activationLevel * 2 &&
                trade.MaxProfitUsd < activationLevel * 4)
            {
                double buffer = atr * profile.Stage2AtrFactor;

                bool stillHealthy = ContinuationValidator.IsHealthyContinuation(symbol,
                    trade.Side == TradeSide.Buy ? TradeAction.EnterBuy : TradeAction.EnterSell,
                    rates1Hr,
                    emaFast,
                    atr);

                if (!stillHealthy && trade.ProfitUsd <= trade.MaxProfitUsd - buffer)
                {
                    LogFile.Write($"[{symbol}] Protection exit (continuation failed)");
                    return true;
                }
            }

            // --------------------------------
            // 🔥 STAGE 3 (RUNNER MODE)
            // --------------------------------
            if (trade.MaxProfitUsd >= activationLevel * 4)
            {
                double buffer = atr * profile.Stage3AtrFactor;

                bool stillHealthy = ContinuationValidator.IsHealthyContinuation(symbol,
                    trade.Side == TradeSide.Buy ? TradeAction.EnterBuy : TradeAction.EnterSell,
                    rates1Hr,
                    emaFast,
                    atr);

                if (!stillHealthy && trade.ProfitUsd <= trade.MaxProfitUsd - buffer)
                {
                    LogFile.Write($"[{symbol}] Protection exit (continuation failed)");
                    return true;
                }
            }

            return false;
        }
    }

}
