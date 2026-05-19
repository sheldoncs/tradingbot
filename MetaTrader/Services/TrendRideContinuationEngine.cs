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
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < 6 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return false;

            var c0 = rates[^2]; // latest closed continuation candle
            var c1 = rates[^3]; // pullback candle

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];

            double ema9Prev = emaFast[^3];
            double ema21Prev = emaSlow[^3];

            string s = symbol.ToUpperInvariant();

            // =============================
            // SYMBOL-ADAPTIVE SETTINGS
            // =============================
            double minAtrPercentile =
                IsFx(s) ? 0.35 :
                IsGold(s) ? 0.40 :
                IsCrypto(s) ? 0.40 :
                IsIndex(s) ? 0.40 :
                0.35;

            double maxTrendRideExtensionAtr =
                IsFx(s) ? 0.90 :
                IsGold(s) ? 1.20 :
                IsCrypto(s) ? 1.50 :
                IsIndex(s) ? 1.30 :
                1.00;

            double maxDistance =
                IsGold(s) ? 1.25 :
                IsCrypto(s) ? 1.45 :
                IsIndex(s) ? 1.15 :
                0.95; // FX/default

            double pullbackToSlowMax =
                IsGold(s) ? MaxPullbackAtr :
                IsCrypto(s) ? 1.7 :
                IsIndex(s) ? 1.6 :
                1.35; // FX/default

            double bodyStrengthRequired =
                IsFx(s) ? 0.38 :
                IsGold(s) ? 0.48 :
                BodyStrength;

            // Normalize ATR percentile if your system sends 40 instead of 0.40
            double normalizedAtrPercentile = atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            // =============================
            // TREND
            // =============================
            bool trendUp =
                emaFast[^2] > emaSlow[^2] &&
                emaFast[^2] > emaFast[^3];

            bool trendDown =
                emaFast[^2] < emaSlow[^2] &&
                emaFast[^2] < emaFast[^3];

            if (!trendUp && !trendDown)
            {
                LogFile.Write($"[{symbol}] TrendRide blocked: no clear trend");
                return false;
            }

            double emaSlope = emaFast[^2] - emaFast[^3];
            bool strongTrend = Math.Abs(emaSlope) > atr * 0.05;

            // =============================
            // ATR REGIME
            // =============================
            if (normalizedAtrPercentile < minAtrPercentile)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: ATR percentile too low | " +
                    $"ATR%={normalizedAtrPercentile:F2}, need>={minAtrPercentile:F2}"
                );
                return false;
            }

            // =============================
            // EXTENSION
            // =============================
            double distanceFromEMA9 = Math.Abs(c0.close - ema9);

            if (!strongTrend && distanceFromEMA9 > atr * maxDistance)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: too far from EMA9 | " +
                    $"dist={distanceFromEMA9:F5}, max={atr * maxDistance:F5}"
                );
                return false;
            }

            // =============================
            // PULLBACK
            // Improved:
            // - Allows close near EMA9
            // - Allows wick touch/rejection around EMA9
            // - Allows controlled pullback toward EMA21
            // =============================
            double tolerance = atr * PullbackAtrFactor;

            bool pullbackCloseNearFast =
                Math.Abs(c1.close - ema9Prev) <= tolerance;

            bool pullbackWickTouchedFast =
                trendUp
                    ? c1.low <= ema9Prev + tolerance && c1.close >= ema9Prev - tolerance
                    : c1.high >= ema9Prev - tolerance && c1.close <= ema9Prev + tolerance;

            bool shallowPullback =
                Math.Abs(c1.close - ema9Prev) <= atr * 1.0;

            bool pulledTowardSlow =
                trendUp
                    ? c1.low <= ema21Prev + atr * 0.25
                    : c1.high >= ema21Prev - atr * 0.25;

            bool validPullback =
                pullbackCloseNearFast ||
                pullbackWickTouchedFast ||
                shallowPullback ||
                pulledTowardSlow;

            if (!validPullback)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: no valid pullback | " +
                    $"close={c1.close:F5}, ema9={ema9Prev:F5}, ema21={ema21Prev:F5}"
                );
                return false;
            }

            // =============================
            // DEPTH CHECK
            // Do not allow pullback to be too far from EMA21.
            // This keeps TrendRide from entering after a messy deep retrace.
            // =============================
            double distToSlow = Math.Abs(c1.close - ema21Prev);

            if (distToSlow > atr * pullbackToSlowMax)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: pullback too deep from EMA21 | " +
                    $"dist={distToSlow:F5}, max={atr * pullbackToSlowMax:F5}"
                );
                return false;
            }

            // =============================
            // CONTINUATION CANDLE
            // =============================
            double range = c0.high - c0.low;
            if (range <= 0) return false;

            double body = Math.Abs(c0.close - c0.open);
            double bodyRatio = body / range;

            bool bullishCandle =
                c0.close > c0.open &&
                bodyRatio >= bodyStrengthRequired;

            bool bearishCandle =
                c0.close < c0.open &&
                bodyRatio >= bodyStrengthRequired;

            if (trendUp && !bullishCandle)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: weak bullish continuation candle | " +
                    $"bodyRatio={bodyRatio:F2}, need>={bodyStrengthRequired:F2}"
                );
                return false;
            }

            if (trendDown && !bearishCandle)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: weak bearish continuation candle | " +
                    $"bodyRatio={bodyRatio:F2}, need>={bodyStrengthRequired:F2}"
                );
                return false;
            }

            // =============================
            // POST-SHOCK / LIQUIDITY WICK GUARD
            // =============================

            if (IsGold(symbol))
            {
                var c2 = rates[^4];

                double c1Range = c1.high - c1.low;
                double c1Body = Math.Abs(c1.close - c1.open);

                double upperWick = c1.high - Math.Max(c1.open, c1.close);
                double lowerWick = Math.Min(c1.open, c1.close) - c1.low;

                bool largeShockCandle = c1Range > atr * 1.8;

                bool largeCounterWick =
                    trendDown
                        ? lowerWick > atr * 0.75   // big buying wick after sell-side flush
                        : upperWick > atr * 0.75;  // big selling wick after buy-side spike

                if (largeShockCandle || largeCounterWick)
                {
                    LogFile.Write(
                        $"[{symbol}] TrendRide blocked: post-shock / liquidity wick stabilization required | " +
                        $"rangeATR={(c1Range / atr):F2}, lowerWickATR={(lowerWick / atr):F2}, upperWickATR={(upperWick / atr):F2}"
                    );
                    return false;
                }

                // =============================
                // OVERLAP / DIGESTION GUARD
                // =============================
                bool overlapping =
                    c0.high <= c1.high &&
                    c0.low >= c1.low;

                if (overlapping)
                {
                    LogFile.Write($"[{symbol}] TrendRide blocked: overlapping continuation candle after pullback.");
                    return false;
                }
            }
            // =============================
            // ENTRY
            // =============================

            double bid = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);

            double ask = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            

            if (trendUp && validPullback && bullishCandle)
            {
                double liveDistanceAtr = Math.Abs(ask - ema9) / atr;

                if (liveDistanceAtr > maxTrendRideExtensionAtr)
                {
                    LogFile.Write($"[{ctx.Symbol}] TrendRide blocked: too extended at execution");
                    return false;
                }

                LogFile.Write(
                    $"[{symbol}] TrendRide BUY | " +
                    $"ATR%={normalizedAtrPercentile:F2}, distEMA9={distanceFromEMA9:F5}, " +
                    $"bodyRatio={bodyRatio:F2}, strongTrend={strongTrend}"
                );

                action = TradeAction.EnterBuy;
                return true;
            }

            if (trendDown && validPullback && bearishCandle)
            {

                double liveDistanceAtr = Math.Abs(bid - ema9) / atr;

                if (liveDistanceAtr > maxTrendRideExtensionAtr)
                {
                    LogFile.Write($"[{ctx.Symbol}] TrendRide blocked: too extended at execution");
                    return false;
                }

                LogFile.Write(
                    $"[{symbol}] TrendRide SELL | " +
                    $"ATR%={normalizedAtrPercentile:F2}, distEMA9={distanceFromEMA9:F5}, " +
                    $"bodyRatio={bodyRatio:F2}, strongTrend={strongTrend}"
                );

                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }

        // =============================
        // SYMBOL HELPERS
        // =============================
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
                   symbol.Contains("LTC");
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

        private static bool IsFx(string symbol)
        {
            return !IsGold(symbol) &&
                   !IsCrypto(symbol) &&
                   !IsIndex(symbol);
        }
    }
}
