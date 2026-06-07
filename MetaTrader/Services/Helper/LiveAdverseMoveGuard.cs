using MetaTrader.Data.Entities;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class LiveAdverseMoveGuard
    {
        private class Profile
        {
            public double HardAdverseAtr;
            public double BodyRatio;
            public double MinProfitUsdForEma9Exit;
            public bool UseEma21HardExit;
        }

        public static bool ShouldClose(
            TrackedTrade trade,
            string symbol,
            double bid,
            double ask,
            double atr,
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow)
        {
            if (trade == null || atr <= 0 ||
                rates == null || rates.Length < 3 ||
                emaFast == null || emaFast.Length < 2 ||
                emaSlow == null || emaSlow.Length < 2)
                return false;

            var profile = GetProfile(symbol);

            var c0 = rates[^1]; // live candle
            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double livePrice = trade.Side == TradeSide.Buy ? bid : ask;

            double adverseMove =
                trade.Side == TradeSide.Buy
                    ? trade.EntryPrice - livePrice
                    : livePrice - trade.EntryPrice;

            double adverseAtr = adverseMove / atr;

            double range = c0.high - c0.low;
            double body = Math.Abs(c0.close - c0.open);
            double bodyRatio = range > 0 ? body / range : 0;

            bool oppositeStrongCandle =
                trade.Side == TradeSide.Buy
                    ? c0.close < c0.open && bodyRatio >= profile.BodyRatio
                    : c0.close > c0.open && bodyRatio >= profile.BodyRatio;

            bool ema9AgainstTrade =
                trade.Side == TradeSide.Buy
                    ? livePrice < ema9
                    : livePrice > ema9;

            bool ema21AgainstTrade =
                trade.Side == TradeSide.Buy
                    ? livePrice < ema21
                    : livePrice > ema21;

            if (adverseAtr >= profile.HardAdverseAtr && oppositeStrongCandle)
            {
                LogFile.Write(
                    $"[{symbol}] LiveAdverse EXIT: hard adverse move | " +
                    $"side={trade.Side}, adverseATR={adverseAtr:F2}, bodyRatio={bodyRatio:F2}"
                );
                return true;
            }

            if (profile.UseEma21HardExit &&
                ema21AgainstTrade &&
                oppositeStrongCandle)
            {
                LogFile.Write(
                    $"[{symbol}] LiveAdverse EXIT: EMA21 reclaimed against trade | " +
                    $"side={trade.Side}, price={livePrice:F5}, ema21={ema21:F5}, bodyRatio={bodyRatio:F2}"
                );
                return true;
            }

            if (trade.MaxProfitUsd >= profile.MinProfitUsdForEma9Exit &&
                ema9AgainstTrade &&
                oppositeStrongCandle)
            {
                LogFile.Write(
                    $"[{symbol}] LiveAdverse EXIT: EMA9 failure after profit | " +
                    $"side={trade.Side}, price={livePrice:F5}, ema9={ema9:F5}, " +
                    $"maxProfit={trade.MaxProfitUsd:F2}"
                );
                return true;
            }

            return false;
        }

        private static Profile GetProfile(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            if (IsGold(s))
            {
                return new Profile
                {
                    HardAdverseAtr = 0.55,
                    BodyRatio = 0.50,
                    MinProfitUsdForEma9Exit = 3.00,
                    UseEma21HardExit = true
                };
            }

            if (IsCrypto(s))
            {
                return new Profile
                {
                    HardAdverseAtr = 0.70,
                    BodyRatio = 0.55,
                    MinProfitUsdForEma9Exit = 2.50,
                    UseEma21HardExit = true
                };
            }

            if (IsIndex(s))
            {
                return new Profile
                {
                    HardAdverseAtr = 0.65,
                    BodyRatio = 0.55,
                    MinProfitUsdForEma9Exit = 3.00,
                    UseEma21HardExit = true
                };
            }

            // FX default
            return new Profile
            {
                HardAdverseAtr = 0.55,
                BodyRatio = 0.52,
                MinProfitUsdForEma9Exit = 2.00,
                UseEma21HardExit = true
            };
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") ||
                   symbol.Contains("XAG") ||
                   symbol.Contains("GOLD") ||
                   symbol.Contains("SILVER");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("LINK");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("USTEC") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("DJ") ||
                   symbol.Contains("SPX") ||
                   symbol.Contains("US500") ||
                   symbol.Contains("GER") ||
                   symbol.Contains("DAX") ||
                   symbol.Contains("UK100");
        }
    }
}
