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

            if (rates == null || rates.Length < 8 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return false;

            // IMPORTANT:
            // Your MqlRates[] array is ordered so rates[^1] is the latest CLOSED candle.
            var c0 = rates[^1]; // latest closed continuation candle
            var c1 = rates[^2]; // pullback candle

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double ema9Prev = emaFast[^2];
            double ema21Prev = emaSlow[^2];

            string s = symbol.ToUpperInvariant();

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
                0.95;

            double pullbackToSlowMax =
                IsGold(s) ? MaxPullbackAtr :
                IsCrypto(s) ? 1.7 :
                IsIndex(s) ? 1.6 :
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
            {
                LogFile.Write($"[{symbol}] TrendRide blocked: no clear trend");
                return false;
            }

            double emaSlope = emaFast[^1] - emaFast[^2];
            bool strongTrend = Math.Abs(emaSlope) > atr * 0.05;

            if (normalizedAtrPercentile < minAtrPercentile)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: ATR percentile too low | " +
                    $"ATR%={normalizedAtrPercentile:F2}, need>={minAtrPercentile:F2}"
                );
                return false;
            }

            double distanceFromEMA9 = Math.Abs(c0.close - ema9);

            if (!strongTrend && distanceFromEMA9 > atr * maxDistance)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: too far from EMA9 | " +
                    $"dist={distanceFromEMA9:F5}, max={atr * maxDistance:F5}"
                );
                return false;
            }

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


            double stallNearEmaAtr =
                IsGold(s) ? 0.28 :
                IsFx(s) ? 0.32 :
                IsIndex(s) ? 0.35 :
                IsCrypto(s) ? 0.40 :
                0.35;

            double smallBodyThreshold =
                IsGold(s) ? 0.40 :
                IsFx(s) ? 0.35 :
                IsIndex(s) ? 0.34 :
                IsCrypto(s) ? 0.30 :
                0.35;

            // =============================
            // EMA9 STALL GUARD - IMPROVED
            // Blocks dead sideways chop near EMA9,
            // but allows valid continuation candles.
            // =============================
            int stallCount = 0;
            int smallBodyCount = 0;

            for (int i = 1; i <= 4 && i < rates.Length && i < emaFast.Length; i++)
            {
                var c = rates[^i];
                double emaAtCandle = emaFast[^i];

                double candleRange = c.high - c.low;
                double candleBody = Math.Abs(c.close - c.open);

                bool nearEma9 =
                    Math.Abs(c.close - emaAtCandle) < atr * 0.35;

                bool smallBody =
                    candleRange > 0 &&
                    (candleBody / candleRange) < 0.35;

                if (nearEma9)
                    stallCount++;

                if (nearEma9 && smallBody)
                    smallBodyCount++;
            }

            bool currentContinuationPush =
                trendUp
                    ? c0.close > c0.open && c0.close > c1.high
                    : c0.close < c0.open && c0.close < c1.low;

            bool priceProgressing =
                trendUp
                    ? c0.close > c1.close
                    : c0.close < c1.close;

            if (stallCount >= 3 &&
                smallBodyCount >= 2 &&
                !currentContinuationPush &&
                !priceProgressing)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: pullback stalled near EMA9 | " +
                    $"stallCount={stallCount}, smallBodyCount={smallBodyCount}"
                );
                return false;
            }

            double distToSlow = Math.Abs(c1.close - ema21Prev);

            if (distToSlow > atr * pullbackToSlowMax)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: pullback too far from EMA21 | " +
                    $"dist={distToSlow:F5}, max={atr * pullbackToSlowMax:F5}"
                );
                return false;
            }

            double range = c0.high - c0.low;
            if (range <= 0)
                return false;

            double body = Math.Abs(c0.close - c0.open);
            double bodyRatio = body / range;

            bool bullishCandle =
                c0.close > c0.open &&
                bodyRatio >= bodyStrengthRequired;

            bool bearishCandle =
                c0.close < c0.open &&
                bodyRatio >= bodyStrengthRequired;

            LogFile.Write(
                $"[{symbol}] TrendRide candle debug | " +
                $"c0Time={c0.time}, open={c0.open:F5}, close={c0.close:F5}, " +
                $"bullish={c0.close > c0.open}, bearish={c0.close < c0.open}, " +
                $"bodyRatio={bodyRatio:F2}, need={bodyStrengthRequired:F2}"
            );

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

            // --------------------------------------------------
            // GOLD ONLY: Late-entry / exhaustion guard
            // Blocks late BUYs after extended bullish moves
            // Blocks late SELLs after extended bearish moves
            // --------------------------------------------------
            if (IsGold(s))
            {
                bool isBuySetup = trendUp && bullishCandle;
                bool isSellSetup = trendDown && bearishCandle;

                if (IsGoldLateEntry(symbol, rates, atr, ema9, isBuySetup, isSellSetup))
                    return false;
            }

            double bid = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = _client.SymbolInfoDouble(ctx.Symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            if (trendUp && validPullback && bullishCandle)
            {
                double liveDistanceAtr = Math.Abs(ask - ema9) / atr;

                if (liveDistanceAtr > maxTrendRideExtensionAtr)
                {
                    LogFile.Write(
                        $"[{ctx.Symbol}] TrendRide blocked: too extended at execution | " +
                        $"liveDistATR={liveDistanceAtr:F2}, max={maxTrendRideExtensionAtr:F2}"
                    );
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
                    LogFile.Write(
                        $"[{ctx.Symbol}] TrendRide blocked: too extended at execution | " +
                        $"liveDistATR={liveDistanceAtr:F2}, max={maxTrendRideExtensionAtr:F2}"
                    );
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

        private static bool IsGoldLateEntry(
            string symbol,
            MqlRates[] rates,
            double atr,
            double ema9,
            bool isBuySetup,
            bool isSellSetup)
        {
            if (rates == null || rates.Length < 8 || atr <= 0)
                return false;

            var c0 = rates[^1];

            double extensionAtr = Math.Abs(c0.close - ema9) / atr;

            int lookback = Math.Min(8, rates.Length - 1);

            double recentHigh = double.MinValue;
            double recentLow = double.MaxValue;

            for (int i = 1; i <= lookback; i++)
            {
                var c = rates[^i];
                recentHigh = Math.Max(recentHigh, c.high);
                recentLow = Math.Min(recentLow, c.low);
            }

            double recentMoveAtr = (recentHigh - recentLow) / atr;

            int sameDirectionCount =
                CountRecentSameDirectionCandles(rates, isBuySetup);

            const double MaxGoldExtensionAtr = 1.55;
            const double MaxGoldRecentMoveAtr = 3.00;
            const int MaxGoldSameDirectionCandles = 4;

            if (extensionAtr > MaxGoldExtensionAtr)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: gold too extended from EMA9 | " +
                    $"extensionATR={extensionAtr:F2}, max={MaxGoldExtensionAtr:F2}"
                );
                return true;
            }

            if (isSellSetup &&
                recentMoveAtr >= MaxGoldRecentMoveAtr &&
                c0.close <= recentLow + atr * 0.85)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide SELL blocked: GOLD late sell after large move | " +
                    $"recentMoveATR={recentMoveAtr:F2}, closeNearLow=true"
                );
                return true;
            }

            if (isBuySetup &&
                recentMoveAtr >= MaxGoldRecentMoveAtr &&
                c0.close >= recentHigh - atr * 0.85)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide BUY blocked: GOLD late buy after large move | " +
                    $"recentMoveATR={recentMoveAtr:F2}, closeNearHigh=true"
                );
                return true;
            }

            if (sameDirectionCount >= MaxGoldSameDirectionCandles)
            {
                LogFile.Write(
                    $"[{symbol}] TrendRide blocked: gold chase protection | " +
                    $"sameDir={sameDirectionCount}"
                );
                return true;
            }

            return false;
        }

        private static int CountRecentSameDirectionCandles(
            MqlRates[] rates,
            bool isBuySetup)
        {
            int count = 0;

            for (int i = rates.Length - 1; i >= rates.Length - 6 && i >= 0; i--)
            {
                var c = rates[i];

                bool bullish = c.close > c.open;
                bool bearish = c.close < c.open;

                if (isBuySetup && bullish)
                    count++;
                else if (!isBuySetup && bearish)
                    count++;
            }

            return count;
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
