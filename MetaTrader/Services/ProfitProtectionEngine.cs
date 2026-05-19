using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Services.Helper;
using MetaTrader.Util;
using MtApi5;
using System;
using static MetaTrader.Services.ExitTrade;

namespace MetaTrader.Services
{

    public static class ProfitProtectionEngine
    {
        private class Profile
        {
            public double ActivationAtrFactor;
            public double Stage2AtrFactor;
            public double Stage3AtrFactor;
            public double TrendExpansionFactor;
            public double MinProfitFloorUsd;
            public double Stage1GivebackPercent;
            public double ReversalBodyRatio;
            public double ReversalWickAtr;
        }

        private static Profile GetProfile(string symbol)
        {
            string s = symbol.ToUpperInvariant();

            if (s.Contains("XAU") || s.Contains("XAG") || s.Contains("GOLD") || s.Contains("SILVER"))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.10,
                    Stage2AtrFactor = 1.75,
                    Stage3AtrFactor = 2.75,
                    TrendExpansionFactor = 1.25,
                    MinProfitFloorUsd = 3.00,
                    Stage1GivebackPercent = 0.55,
                    ReversalBodyRatio = 0.48,
                    ReversalWickAtr = 0.55
                };
            }

            if (IsCrypto(s))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.10,
                    Stage2AtrFactor = 1.65,
                    Stage3AtrFactor = 2.75,
                    TrendExpansionFactor = 1.25,
                    MinProfitFloorUsd = 2.50,
                    Stage1GivebackPercent = 0.50,
                    ReversalBodyRatio = 0.45,
                    ReversalWickAtr = 0.50
                };
            }

            if (s.Contains("US100") || s.Contains("NAS") || s.Contains("SPX") || s.Contains("US30") || s.Contains("US500"))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.15,
                    Stage2AtrFactor = 1.80,
                    Stage3AtrFactor = 2.90,
                    TrendExpansionFactor = 1.25,
                    MinProfitFloorUsd = 3.00,
                    Stage1GivebackPercent = 0.55,
                    ReversalBodyRatio = 0.45,
                    ReversalWickAtr = 0.50
                };
            }

            return new Profile
            {
                ActivationAtrFactor = 1.00,
                Stage2AtrFactor = 1.60,
                Stage3AtrFactor = 2.40,
                TrendExpansionFactor = 1.15,
                MinProfitFloorUsd = 2.50,
                Stage1GivebackPercent = 0.55,
                ReversalBodyRatio = 0.45,
                ReversalWickAtr = 0.45
            };
        }

        public static bool ShouldCloseTrade(
            TrackedTrade trade,
            string symbol,
            double atr,
            MqlRates[] rates1Hr,
            double[] emaFast)
        {
            if (trade == null || atr <= 0)
                return false;

            if (rates1Hr == null || rates1Hr.Length < 3)
                return false;

            if (emaFast == null || emaFast.Length < 3)
                return false;

            var profile = GetProfile(symbol);

            trade.MaxProfitUsd = Math.Max(trade.MaxProfitUsd, trade.ProfitUsd);

            LogFile.Write(
                $"[{symbol}] PPE reached | side={trade.Side}, model={trade.Model}, " +
                $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}"
            );

            if (trade.ProfitUsd <= 0)
                return false;

            double currentPrice = rates1Hr[^1].close;

            double moveFromEntry =
                trade.Side == TradeSide.Buy
                    ? currentPrice - trade.EntryPrice
                    : trade.EntryPrice - currentPrice;

            if (moveFromEntry <= 0)
                return false;

            double activationMove = atr * profile.ActivationAtrFactor;

            if (moveFromEntry < activationMove)
            {
                LogFile.Write(
                    $"[{symbol}] PPE hold: activation not reached | " +
                    $"moveATR={(moveFromEntry / atr):F2}, needATR={profile.ActivationAtrFactor:F2}"
                );
                return false;
            }

            if (trade.MaxProfitUsd < profile.MinProfitFloorUsd)
            {
                LogFile.Write(
                    $"[{symbol}] PPE hold: max profit below floor | " +
                    $"max={trade.MaxProfitUsd:F2}, need={profile.MinProfitFloorUsd:F2}"
                );
                return false;
            }

            var c0 = rates1Hr[^1];

            double range = c0.high - c0.low;
            double body = Math.Abs(c0.close - c0.open);
            double bodyRatio = range > 0 ? body / range : 0;

            double upperWick = c0.high - Math.Max(c0.open, c0.close);
            double lowerWick = Math.Min(c0.open, c0.close) - c0.low;

            bool oppositeStrongCandle =
                trade.Side == TradeSide.Sell
                    ? c0.close > c0.open && bodyRatio >= profile.ReversalBodyRatio
                    : c0.close < c0.open && bodyRatio >= profile.ReversalBodyRatio;

            bool oppositeRejectionWick =
                trade.Side == TradeSide.Sell
                    ? lowerWick >= atr * profile.ReversalWickAtr
                    : upperWick >= atr * profile.ReversalWickAtr;

            double retainedProfitPercent =
                trade.MaxProfitUsd > 0
                    ? trade.ProfitUsd / trade.MaxProfitUsd
                    : 1.0;

            if ((oppositeStrongCandle || oppositeRejectionWick) &&
                retainedProfitPercent <= 0.80)
            {
                LogFile.Write(
                    $"[{symbol}] Protection exit: reversal/giveback detected | " +
                    $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, " +
                    $"retained={retainedProfitPercent:P0}, bodyRatio={bodyRatio:F2}, " +
                    $"upperWickATR={(upperWick / atr):F2}, lowerWickATR={(lowerWick / atr):F2}"
                );

                return true;
            }

            double emaSlope = Math.Abs(emaFast[^1] - emaFast[^2]);
            bool strongTrend = emaSlope > atr * 0.06;

            double moveAtr = moveFromEntry / atr;

            if (moveAtr < profile.ActivationAtrFactor * 2)
            {
                double floor = trade.MaxProfitUsd * profile.Stage1GivebackPercent;

                if (trade.ProfitUsd <= floor)
                {
                    LogFile.Write(
                        $"[{symbol}] Protection exit (Stage 1) | " +
                        $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, floor={floor:F2}"
                    );
                    return true;
                }

                return false;
            }

            if (moveAtr < profile.ActivationAtrFactor * 4)
            {
                double buffer = atr * profile.Stage2AtrFactor;

                if (strongTrend)
                    buffer *= profile.TrendExpansionFactor;

                double estimatedUsdBuffer = buffer * trade.LotSize;
                double exitLine = trade.MaxProfitUsd - estimatedUsdBuffer;

                if (trade.ProfitUsd <= exitLine)
                {
                    LogFile.Write(
                        $"[{symbol}] Protection exit (Stage 2) | " +
                        $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, exitLine={exitLine:F2}"
                    );
                    return true;
                }

                return false;
            }

            double runnerBuffer = atr * profile.Stage3AtrFactor;

            if (strongTrend)
                runnerBuffer *= profile.TrendExpansionFactor;

            double runnerUsdBuffer = runnerBuffer * trade.LotSize;
            double runnerExitLine = trade.MaxProfitUsd - runnerUsdBuffer;

            if (trade.ProfitUsd <= runnerExitLine)
            {
                LogFile.Write(
                    $"[{symbol}] Protection exit (Runner) | " +
                    $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, exitLine={runnerExitLine:F2}"
                );
                return true;
            }

            return false;
        }

        private static bool IsCrypto(string symbol)
        {
            string s = symbol.ToUpperInvariant();

            return s.Contains("BTC") ||
                   s.Contains("ETH") ||
                   s.Contains("SOL") ||
                   s.Contains("AVAX") ||
                   s.Contains("BNB") ||
                   s.Contains("XRP") ||
                   s.Contains("ADA") ||
                   s.Contains("DOGE") ||
                   s.Contains("LTC");
        }
    }

}
