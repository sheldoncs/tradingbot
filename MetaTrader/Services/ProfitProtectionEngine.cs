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
            public double Stage1AtrFactor;
            public double Stage2AtrFactor;
            public double Stage3AtrFactor;
            public double TrendExpansionFactor;

            public double MinProfitFloorUsd;
            public double Stage1GivebackPercent;
        }

        // --------------------------------
        // PROFILE PER ASSET
        // --------------------------------
        private static Profile GetProfile(string symbol)
        {
            string s = symbol.ToUpperInvariant();

            if (s.Contains("XAU") || s.Contains("XAG") || s.Contains("GOLD") || s.Contains("SILVER"))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.10,
                    Stage1AtrFactor = 1.10,
                    Stage2AtrFactor = 1.75,
                    Stage3AtrFactor = 2.75,
                    TrendExpansionFactor = 1.25,
                    MinProfitFloorUsd = 3.00,
                    Stage1GivebackPercent = 0.55
                };
            }

            if (s.Contains("US100") ||
                s.Contains("NAS") ||
                s.Contains("SPX") ||
                s.Contains("US30") ||
                s.Contains("US500"))
            {
                return new Profile
                {
                    ActivationAtrFactor = 1.20,
                    Stage1AtrFactor = 1.20,
                    Stage2AtrFactor = 1.90,
                    Stage3AtrFactor = 3.00,
                    TrendExpansionFactor = 1.30,
                    MinProfitFloorUsd = 3.00,
                    Stage1GivebackPercent = 0.60
                };
            }

            if (IsCrypto(s))
            {
                return new Profile
                {
                    // Previous crypto profile was very wide:
                    // 1.6 / 2.5 / 3.8 / 1.60
                    // This is still breathable, but protects earlier.
                    ActivationAtrFactor = 1.20,
                    Stage1AtrFactor = 1.20,
                    Stage2AtrFactor = 1.80,
                    Stage3AtrFactor = 3.00,
                    TrendExpansionFactor = 1.35,
                    MinProfitFloorUsd = 2.50,
                    Stage1GivebackPercent = 0.55
                };
            }

            // FX default
            return new Profile
            {
                ActivationAtrFactor = 1.00,
                Stage1AtrFactor = 1.10,
                Stage2AtrFactor = 1.70,
                Stage3AtrFactor = 2.50,
                TrendExpansionFactor = 1.20,
                MinProfitFloorUsd = 2.50,
                Stage1GivebackPercent = 0.55
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

            string s = symbol.ToUpperInvariant();
            var profile = GetProfile(s);

            // --------------------------------
            // TRACK MAX PROFIT
            // --------------------------------
            trade.MaxProfitUsd = Math.Max(
                trade.MaxProfitUsd,
                trade.ProfitUsd);

            // --------------------------------
            // NEVER CLOSE LOSING TRADE
            // --------------------------------
            if (trade.ProfitUsd <= 0)
            {
                LogFile.Write(
                    $"[{symbol}] ProfitProtection hold: trade not profitable | " +
                    $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}"
                );

                return false;
            }

            // --------------------------------
            // CURRENT PRICE
            // rates1Hr[^1] is usually the current/live candle.
            // --------------------------------
            double currentPrice = rates1Hr[^1].close;

            // --------------------------------
            // DIRECTION-AWARE MOVE FROM ENTRY
            // --------------------------------
            double moveFromEntry =
                trade.Side == TradeSide.Buy
                    ? currentPrice - trade.EntryPrice
                    : trade.EntryPrice - currentPrice;

            if (moveFromEntry <= 0)
            {
                LogFile.Write(
                    $"[{symbol}] ProfitProtection hold: structural move not positive | " +
                    $"side={trade.Side}, entry={trade.EntryPrice:F5}, price={currentPrice:F5}, " +
                    $"move={moveFromEntry:F5}, profit={trade.ProfitUsd:F2}"
                );

                return false;
            }

            // --------------------------------
            // MINIMUM STRUCTURAL MOVE
            // --------------------------------
            double activationMove = atr * profile.ActivationAtrFactor;

            if (moveFromEntry < activationMove)
            {
                LogFile.Write(
                    $"[{symbol}] ProfitProtection hold: activation move not reached | " +
                    $"moveATR={(moveFromEntry / atr):F2}, needATR={profile.ActivationAtrFactor:F2}, " +
                    $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}"
                );

                return false;
            }

            // --------------------------------
            // MINIMUM PROFIT FLOOR
            // --------------------------------
            if (trade.MaxProfitUsd < profile.MinProfitFloorUsd)
            {
                LogFile.Write(
                    $"[{symbol}] ProfitProtection hold: max profit below floor | " +
                    $"max={trade.MaxProfitUsd:F2}, need={profile.MinProfitFloorUsd:F2}, " +
                    $"profit={trade.ProfitUsd:F2}"
                );

                return false;
            }

            // --------------------------------
            // TREND STRENGTH DETECTION
            // --------------------------------
            double emaSlope = Math.Abs(emaFast[^1] - emaFast[^2]);
            bool strongTrend = emaSlope > atr * 0.06;

            double moveAtr = moveFromEntry / atr;

            LogFile.Write(
                $"[{symbol}] ProfitProtection eval | " +
                $"side={trade.Side}, profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, " +
                $"moveATR={moveAtr:F2}, activationATR={profile.ActivationAtrFactor:F2}, " +
                $"strongTrend={strongTrend}"
            );

            // --------------------------------
            // STAGE 1 — EARLY PROFIT DEFENSE
            // --------------------------------
            if (moveFromEntry >= activationMove &&
                moveFromEntry < activationMove * 2)
            {
                double protectedFloor =
                    trade.MaxProfitUsd * profile.Stage1GivebackPercent;

                if (trade.ProfitUsd <= protectedFloor)
                {
                    LogFile.Write(
                        $"[{symbol}] Protection exit (Stage 1) | " +
                        $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, " +
                        $"floor={protectedFloor:F2}, givebackPct={profile.Stage1GivebackPercent:F2}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{symbol}] ProfitProtection hold: Stage 1 breathing | " +
                    $"profit={trade.ProfitUsd:F2}, floor={protectedFloor:F2}, max={trade.MaxProfitUsd:F2}"
                );

                return false;
            }

            // --------------------------------
            // STAGE 2 — LIGHT TRAILING
            // --------------------------------
            if (moveFromEntry >= activationMove * 2 &&
                moveFromEntry < activationMove * 4)
            {
                double buffer = atr * profile.Stage2AtrFactor;

                if (strongTrend)
                    buffer *= profile.TrendExpansionFactor;

                // NOTE:
                // This is approximate. If your MT5 symbol contract sizing is unusual,
                // consider replacing this with proper tick-value conversion later.
                double estimatedUsdBuffer = buffer * trade.LotSize;

                double exitLine =
                    trade.MaxProfitUsd - estimatedUsdBuffer;

                if (trade.ProfitUsd <= exitLine)
                {
                    LogFile.Write(
                        $"[{symbol}] Protection exit (Stage 2) | " +
                        $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, " +
                        $"bufferUsd={estimatedUsdBuffer:F2}, exitLine={exitLine:F2}, " +
                        $"strongTrend={strongTrend}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{symbol}] ProfitProtection hold: Stage 2 trailing | " +
                    $"profit={trade.ProfitUsd:F2}, exitLine={exitLine:F2}, " +
                    $"bufferUsd={estimatedUsdBuffer:F2}, strongTrend={strongTrend}"
                );

                return false;
            }

            // --------------------------------
            // STAGE 3 — RUNNER MODE
            // --------------------------------
            if (moveFromEntry >= activationMove * 4)
            {
                double buffer = atr * profile.Stage3AtrFactor;

                if (strongTrend)
                    buffer *= profile.TrendExpansionFactor;

                double estimatedUsdBuffer = buffer * trade.LotSize;

                double exitLine =
                    trade.MaxProfitUsd - estimatedUsdBuffer;

                if (trade.ProfitUsd <= exitLine)
                {
                    LogFile.Write(
                        $"[{symbol}] Protection exit (Runner) | " +
                        $"profit={trade.ProfitUsd:F2}, max={trade.MaxProfitUsd:F2}, " +
                        $"bufferUsd={estimatedUsdBuffer:F2}, exitLine={exitLine:F2}, " +
                        $"strongTrend={strongTrend}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{symbol}] ProfitProtection hold: Runner mode | " +
                    $"profit={trade.ProfitUsd:F2}, exitLine={exitLine:F2}, " +
                    $"bufferUsd={estimatedUsdBuffer:F2}, strongTrend={strongTrend}"
                );

                return false;
            }

            return false;
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC");
        }
    }

}
