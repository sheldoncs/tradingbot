using AutoMapper;
using MetaTrader.Enums;
using MetaTrader.Services.Filter;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Buffers;
using System.Security.Cryptography;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MetaTrader.Services
{
    public static class TrendRideContinuationEngine
    {
        public static double PullbackAtrFactor = 0.7;
        public static double MaxPullbackAtr = 1.5;
        public static double BodyStrength = 0.40;

        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double atrPercentile,
            SymbolContext ctx,
            MtApi5Client _client,
            double momentum,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < 8 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return false;

            var c0 = rates[^1];
            var c1 = rates[^2];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double ema9Prev = emaFast[^2];
            double ema21Prev = emaSlow[^2];

            string s = symbol.ToUpperInvariant();

            bool isIndex = IsIndex(s);

            double minAtrPercentile =
                IsFx(s) ? 0.30 :
                IsGold(s) ? 0.35 :
                IsCrypto(s) ? 0.40 :
                isIndex ? 0.40 :
                0.35;

            double maxDistance =
                IsGold(s) ? 1.25 :
                IsCrypto(s) ? 1.45 :
                isIndex ? 1.10 :   // 🔥 tighter for indices
                0.95;

            double pullbackToSlowMax =
                IsGold(s) ? MaxPullbackAtr :
                IsCrypto(s) ? 1.7 :
                isIndex ? 1.5 :
                1.35;

            double bodyStrengthRequired =
                IsGold(s) ? 0.50 :
                BodyStrength;

            double normalizedAtrPercentile = atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            bool trendUp =
                emaFast[^1] > emaSlow[^1] &&
                emaFast[^1] > emaFast[^2];

            bool trendDown =
                emaFast[^1] < emaSlow[^1] &&
                emaFast[^1] < emaFast[^2];

            if (!trendUp && !trendDown)
                return false;

            if (normalizedAtrPercentile < minAtrPercentile)
                return false;

            double distanceFromEMA9 = Math.Abs(c0.close - ema9) / atr;

            // 🔥 INDEX: BLOCK LATE ENTRIES
            if (isIndex && distanceFromEMA9 > 1.2)
            {
                LogFile.Write($"[{symbol}] Blocked: late TrendRide (index)");
                return false;
            }

            if (!isIndex && distanceFromEMA9 > maxDistance)
                return false;

            double tolerance = atr * PullbackAtrFactor;

            bool validPullback =
                Math.Abs(c1.close - ema9Prev) <= tolerance ||
                Math.Abs(c1.close - ema9Prev) <= atr ||
                (trendUp ? c1.low <= ema21Prev : c1.high >= ema21Prev);

            if (!validPullback)
                return false;

            double distToSlow = Math.Abs(c1.close - ema21Prev);

            if (distToSlow > atr * pullbackToSlowMax)
                return false;

            double range = c0.high - c0.low;
            if (range <= 0) return false;

            double body = Math.Abs(c0.close - c0.open);
            double bodyRatio = body / range;

            bool bullishCandle = c0.close > c0.open && bodyRatio >= bodyStrengthRequired;
            bool bearishCandle = c0.close < c0.open && bodyRatio >= bodyStrengthRequired;

            // 🔥 INDEX: EXHAUSTION FILTER
            if (isIndex)
            {
                int strongMoves = 0;

                for (int i = 1; i <= 4; i++)
                {
                    var c = rates[^i];
                    double b = Math.Abs(c.close - c.open);
                    double r = c.high - c.low;

                    if (r > 0 && (b / r) > 0.6)
                        strongMoves++;
                }

                if (strongMoves >= 3)
                {
                    LogFile.Write($"[{symbol}] Blocked: exhaustion move (index)");
                    return false;
                }
            }

            double bid = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            double executionPrice = ask;

            // =========================
            // BUY
            // =========================
            if (trendUp && validPullback && bullishCandle)
            {
                if (!HasEnoughSpace(symbol, rates, atr, true))
                    return false;

                action = TradeAction.EnterBuy;

                if (SupportResistanceEngine.ShouldBlockTrade(
                    symbol,
                    action,
                    rates,
                    atr,
                    momentum,
                    executionPrice, ema9))
                {
                    return false;
                }

                return true;
            }

            // =========================
            // SELL
            // =========================
            if (trendDown && validPullback && bearishCandle)
            {
                if (!HasEnoughSpace(symbol, rates, atr, false))
                    return false;

                action = TradeAction.EnterSell;

                if (SupportResistanceEngine.ShouldBlockTrade(
                    symbol,
                    action,
                    rates,
                    atr,
                    momentum,
                    executionPrice, ema9))
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        private static bool HasEnoughSpace(
            string symbol,
            MqlRates[] rates,
            double atr,
            bool isBuy)
        {
            if (rates.Length < 10)
                return true;

            int lookback = 8;

            double recentHigh = double.MinValue;
            double recentLow = double.MaxValue;

            for (int i = 1; i <= lookback; i++)
            {
                var c = rates[^i];
                recentHigh = Math.Max(recentHigh, c.high);
                recentLow = Math.Min(recentLow, c.low);
            }

            double currentPrice = rates[^1].close;

            double minSpaceAtr =
                IsGold(symbol) ? 0.8 :
                IsCrypto(symbol) ? 0.7 :
                IsIndex(symbol) ? 0.7 :   // 🔥 slightly stricter
                0.5;

            if (isBuy)
            {
                if (recentHigh - currentPrice < atr * minSpaceAtr)
                    return false;
            }
            else
            {
                if (currentPrice - recentLow < atr * minSpaceAtr)
                    return false;
            }

            return true;
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") || symbol.Contains("XAG");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") || symbol.Contains("ETH") ||
                   symbol.Contains("SOL") || symbol.Contains("BNB");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") || symbol.Contains("NAS");
        }

        private static bool IsFx(string symbol)
        {
            return !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);
        }
    }
}
