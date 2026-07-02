using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class MarketStateEngine
    {
        public static MarketState DetectState(
            string symbol,
            MqlRates[] rates,
            double[] emaFast,
            double[] emaSlow,
            double atr)
        {
            if (rates == null || rates.Length < 10 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return MarketState.Chop;

            var c0 = rates[^1];
            var c1 = rates[^2];

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];
            double ema9Prev = emaFast[^2];

            double body = Math.Abs(c0.close - c0.open);
            double range = c0.high - c0.low;
            double bodyRatio = range > 0 ? body / range : 0;

            bool bullishCandle = c0.close > c0.open;
            bool bearishCandle = c0.close < c0.open;

            // =============================
            // 1️⃣ TREND DETECTION
            // =============================
            bool trendUp =
                ema9 > ema21 &&
                ema9 > ema9Prev &&
                c0.close > ema9;

            bool trendDown =
                ema9 < ema21 &&
                ema9 < ema9Prev &&
                c0.close < ema9;

            double emaSlope = Math.Abs(ema9 - ema9Prev);
            bool strongTrend = emaSlope > atr * 0.05;

            if ((trendUp || trendDown) && strongTrend)
            {
                LogFile.Write(
                    $"[{symbol}] MarketState = TREND | " +
                    $"ema9={ema9:F5}, ema21={ema21:F5}, slopeAtr={(emaSlope / atr):F2}"
                );

                return MarketState.Trend;
            }

            // =============================
            // 1.5️⃣ EARLY BREAKOUT / ESCAPE
            // =============================

            int breakoutLookback = 6;

            double recentHigh = double.MinValue;
            double recentLow = double.MaxValue;

            for (int i = 2; i <= breakoutLookback + 1; i++)
            {
                var c = rates[^i];
                recentHigh = Math.Max(recentHigh, c.high);
                recentLow = Math.Min(recentLow, c.low);
            }

            double breakoutBuffer = atr * 0.05;
            double emaSpreadAtr = Math.Abs(ema9 - ema21) / atr;

            // 🔥 ASSET-SPECIFIC SPREAD TUNING
            double minSpread =
                IsGold(symbol) ? 0.05 :
                IsFx(symbol) ? 0.05 :
                IsIndex(symbol) ? 0.06 :
                IsCrypto(symbol) ? 0.08 :
                0.05;

            double maxSpread =
                IsGold(symbol) ? 0.25 :
                IsFx(symbol) ? 0.20 :
                IsIndex(symbol) ? 0.30 :
                IsCrypto(symbol) ? 0.35 :
                0.20;

            bool strongBullBody =
                bullishCandle &&
                body >= atr * 0.35 &&
                bodyRatio >= 0.45;

            bool strongBearBody =
                bearishCandle &&
                body >= atr * 0.35 &&
                bodyRatio >= 0.45;

            // Clean breakout
            bool earlyBreakUp =
                c0.close > recentHigh + breakoutBuffer &&
                c0.close > ema9 &&
                c0.close > ema21 &&
                strongBullBody;

            bool earlyBreakDown =
                c0.close < recentLow - breakoutBuffer &&
                c0.close < ema9 &&
                c0.close < ema21 &&
                strongBearBody;

            // Escape moves
            bool bullishEscape =
                c0.close > c1.high &&
                c0.close > ema9 &&
                c0.close > ema21 &&
                strongBullBody;

            bool bearishEscape =
                c0.close < c1.low &&
                c0.close < ema9 &&
                c0.close < ema21 &&
                strongBearBody;

            // 🔥 FINAL FILTERED LOGIC
            bool isTrueEarlyBreak =
                (earlyBreakUp || earlyBreakDown) &&
                emaSpreadAtr < maxSpread;

            bool isValidEscape =
                (bullishEscape || bearishEscape) &&
                emaSpreadAtr > minSpread &&
                emaSpreadAtr < maxSpread;

            if (isTrueEarlyBreak || isValidEscape)
            {
                LogFile.Write(
                    $"[{symbol}] MarketState = EARLY_BREAKOUT | " +
                    $"earlyUp={earlyBreakUp}, earlyDown={earlyBreakDown}, " +
                    $"bullEscape={bullishEscape}, bearEscape={bearishEscape}, " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}, minSpread={minSpread:F2}, maxSpread={maxSpread:F2}, " +
                    $"bodyAtr={(body / atr):F2}, bodyRatio={bodyRatio:F2}, " +
                    $"close={c0.close:F5}, recentHigh={recentHigh:F5}, recentLow={recentLow:F5}"
                );

                return MarketState.EarlyBreakout;
            }

            // =============================
            // 2️⃣ COMPRESSION DETECTION
            // =============================

            int lookback = 6;

            double totalRange = 0;
            double maxRange = 0;
            double minRange = double.MaxValue;

            int bullish = 0;
            int bearish = 0;

            for (int i = 1; i <= lookback; i++)
            {
                var c = rates[^i];
                double r = c.high - c.low;

                totalRange += r;
                maxRange = Math.Max(maxRange, r);
                minRange = Math.Min(minRange, r);

                if (c.close > c.open) bullish++;
                else if (c.close < c.open) bearish++;
            }

            double avgRange = totalRange / lookback;

            bool tight = avgRange < atr * 0.75;
            bool consistent = (maxRange - minRange) < atr * 0.5;

            int diff = Math.Abs(bullish - bearish);
            bool directionalAgreement = diff >= 2;

            if (tight && consistent && directionalAgreement)
            {
                LogFile.Write(
                    $"[{symbol}] MarketState = COMPRESSION | " +
                    $"avgRangeAtr={(avgRange / atr):F2}, rangeSpreadAtr={((maxRange - minRange) / atr):F2}, " +
                    $"bullish={bullish}, bearish={bearish}"
                );

                return MarketState.Compression;
            }

            // =============================
            // 3️⃣ CHOP
            // =============================

            LogFile.Write(
                $"[{symbol}] MarketState = CHOP | " +
                $"trendUp={trendUp}, trendDown={trendDown}, strongTrend={strongTrend}, " +
                $"emaSpreadAtr={emaSpreadAtr:F2}, bodyAtr={(body / atr):F2}, bodyRatio={bodyRatio:F2}, " +
                $"close={c0.close:F5}, recentHigh={recentHigh:F5}, recentLow={recentLow:F5}"
            );

            return MarketState.Chop;
        }

        // =============================
        // HELPERS
        // =============================

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") || symbol.Contains("XAG");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("SPX") ||
                   symbol.Contains(".CASH");
        }

        private static bool IsFx(string symbol)
        {
            return !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);
        }
    }
}
