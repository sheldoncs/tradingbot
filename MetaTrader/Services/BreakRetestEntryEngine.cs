using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;

namespace MetaTrader.Services
{
    using System;
    using System.Linq;

    public static class BreakRetestEntryEngine
    {
        private class Profile
        {
            public int LookbackCandles;
            public double RetestToleranceFactor;
            public double RejectionWickRatio;
            public double MaxOppositeWickRatio;
            public double MaxDistanceFromEma9Atr;
            public double ExhaustionMoveAtr;
            public double EmaRetestToleranceAtr;
            public double BreakBufferAtr;
            public double MinBreakBodyRatio;
        }

        public static int LookbackCandles = 20;
        public static double RetestToleranceFactor = 0.30;
        public static double RejectionBodyRatio = 0.50;

        private static int breakTestCounter = 0;

        public static void retestCounter()
        {
            LogFile.Write($"Break test entries count: {breakTestCounter + 1}");
            breakTestCounter++;
        }

        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (string.IsNullOrWhiteSpace(symbol) ||
                rates == null ||
                emaFast == null ||
                emaSlow == null ||
                atr <= 0 ||
                emaFast.Length < 5 ||
                emaSlow.Length < 5)
            {
                return false;
            }

            var profile = GetProfile(symbol);

            int lookback = Math.Min(LookbackCandles, profile.LookbackCandles);

            if (rates.Length < lookback + 8)
                return false;

            // ============================
            // CANDLES
            // ============================
            var last = rates[^1];       // retest / rejection candle
            var prev = rates[^2];       // break candle candidate
            var prev2 = rates[^3];      // backup break candle candidate

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double slope = emaFast[^1] - emaFast[^2];

            bool bearishTrend =
                emaFast[^1] < emaSlow[^1] &&
                slope <= atr * 0.01;

            bool bullishTrend =
                emaFast[^1] > emaSlow[^1] &&
                slope >= -atr * 0.01;

            if (!bearishTrend && !bullishTrend)
            {
                LogFile.Write($"[{symbol}] BreakRetest blocked: no clean trend");
                return false;
            }

            // ============================
            // STRUCTURE LEVELS
            // Exclude last/retest candle and recent break candidates.
            // This prevents the break candle from becoming its own structure.
            // ============================
            var structureCandles = rates
                .Take(rates.Length - 3)
                .Skip(Math.Max(0, rates.Length - 3 - lookback))
                .ToArray();

            if (structureCandles.Length < lookback)
            {
                LogFile.Write($"[{symbol}] BreakRetest blocked: insufficient structure candles");
                return false;
            }

            double recentHigh = structureCandles.Max(r => r.high);
            double recentLow = structureCandles.Min(r => r.low);

            double tolerance = atr * profile.RetestToleranceFactor;
            double breakBuffer = atr * profile.BreakBufferAtr;

            // ============================
            // EMA EXTENSION GUARD
            // ============================
            double distanceFromEMA9 = Math.Abs(last.close - ema9);

            if (distanceFromEMA9 > atr * profile.MaxDistanceFromEma9Atr)
            {
                LogFile.Write(
                    $"[{symbol}] BreakRetest blocked: too extended from EMA9 | " +
                    $"dist={distanceFromEMA9:F5}, max={atr * profile.MaxDistanceFromEma9Atr:F5}"
                );
                return false;
            }

            // ============================
            // EXHAUSTION GUARD
            // ============================
            double recentMove = Math.Abs(last.close - rates[^6].close);

            if (recentMove > atr * profile.ExhaustionMoveAtr)
            {
                LogFile.Write(
                    $"[{symbol}] BreakRetest blocked: recent move exhausted | " +
                    $"move={recentMove:F5}, max={atr * profile.ExhaustionMoveAtr:F5}"
                );
                return false;
            }

            // ============================
            // CANDLE QUALITY
            // ============================
            double range = last.high - last.low;
            if (range <= 0)
                return false;

            double body = Math.Abs(last.close - last.open);
            double bodyRatio = body / range;

            double upperWick = last.high - Math.Max(last.open, last.close);
            double lowerWick = Math.Min(last.open, last.close) - last.low;

            double upperWickRatio = upperWick / range;
            double lowerWickRatio = lowerWick / range;

            // ============================
            // BREAK CANDLE QUALITY
            // ============================
            bool prevBearBreakBodyOk = IsBodyStrong(prev, profile.MinBreakBodyRatio);
            bool prevBullBreakBodyOk = IsBodyStrong(prev, profile.MinBreakBodyRatio);

            bool prev2BearBreakBodyOk = IsBodyStrong(prev2, profile.MinBreakBodyRatio);
            bool prev2BullBreakBodyOk = IsBodyStrong(prev2, profile.MinBreakBodyRatio);

            // Allow break to happen on prev OR prev2.
            // This prevents missing trades where the retest happens one candle later.
            bool brokeBelow =
                (
                    prev.close < recentLow - breakBuffer &&
                    prev.close < prev.open &&
                    prevBearBreakBodyOk
                )
                ||
                (
                    prev2.close < recentLow - breakBuffer &&
                    prev2.close < prev2.open &&
                    prev2BearBreakBodyOk
                );

            bool brokeAbove =
                (
                    prev.close > recentHigh + breakBuffer &&
                    prev.close > prev.open &&
                    prevBullBreakBodyOk
                )
                ||
                (
                    prev2.close > recentHigh + breakBuffer &&
                    prev2.close > prev2.open &&
                    prev2BullBreakBodyOk
                );

            // ============================
            // RETEST ZONE
            // Allows wick interaction around the broken level.
            // ============================
            bool retestZoneSell =
                last.high >= recentLow - tolerance &&
                last.low <= recentLow + tolerance;

            bool retestZoneBuy =
                last.low <= recentHigh + tolerance &&
                last.high >= recentHigh - tolerance;

            // ============================
            // REJECTION CONFIRMATION
            // ============================
            bool bearishRejection =
                last.close < last.open &&
                upperWickRatio >= profile.RejectionWickRatio &&
                lowerWickRatio <= profile.MaxOppositeWickRatio &&
                last.close < recentLow + tolerance;

            bool bullishRejection =
                last.close > last.open &&
                lowerWickRatio >= profile.RejectionWickRatio &&
                upperWickRatio <= profile.MaxOppositeWickRatio &&
                last.close > recentHigh - tolerance;

            // Softer acceptance:
            // If wick rejection is not huge, allow strong body close away from level.
            bool bearishBodyReject =
                last.close < last.open &&
                bodyRatio >= 0.45 &&
                last.close < recentLow;

            bool bullishBodyReject =
                last.close > last.open &&
                bodyRatio >= 0.45 &&
                last.close > recentHigh;

            // ============================
            // EMA RETEST CONFIRMATION
            // ============================
            bool emaRejectSell =
                last.high >= ema9 - atr * profile.EmaRetestToleranceAtr ||
                last.high >= ema21 - atr * profile.EmaRetestToleranceAtr;

            bool emaRejectBuy =
                last.low <= ema9 + atr * profile.EmaRetestToleranceAtr ||
                last.low <= ema21 + atr * profile.EmaRetestToleranceAtr;

            // ============================
            // SELL ENTRY
            // ============================
            if (bearishTrend &&
                brokeBelow &&
                retestZoneSell &&
                (bearishRejection || bearishBodyReject) &&
                emaRejectSell)
            {
                retestCounter();

                LogFile.Write(
                    $"[{symbol}] BreakRetest SELL triggered | " +
                    $"level={recentLow:F5}, high={last.high:F5}, close={last.close:F5}, " +
                    $"upperWick={upperWickRatio:F2}, bodyRatio={bodyRatio:F2}"
                );

                action = TradeAction.EnterSell;
                return true;
            }

            // ============================
            // BUY ENTRY
            // ============================
            if (bullishTrend &&
                brokeAbove &&
                retestZoneBuy &&
                (bullishRejection || bullishBodyReject) &&
                emaRejectBuy)
            {
                retestCounter();

                LogFile.Write(
                    $"[{symbol}] BreakRetest BUY triggered | " +
                    $"level={recentHigh:F5}, low={last.low:F5}, close={last.close:F5}, " +
                    $"lowerWick={lowerWickRatio:F2}, bodyRatio={bodyRatio:F2}"
                );

                action = TradeAction.EnterBuy;
                return true;
            }

            LogFile.Write(
                $"[{symbol}] BreakRetest blocked | " +
                $"bullTrend={bullishTrend}, bearTrend={bearishTrend}, " +
                $"brokeAbove={brokeAbove}, brokeBelow={brokeBelow}, " +
                $"retestBuy={retestZoneBuy}, retestSell={retestZoneSell}, " +
                $"bullReject={bullishRejection || bullishBodyReject}, bearReject={bearishRejection || bearishBodyReject}, " +
                $"emaBuy={emaRejectBuy}, emaSell={emaRejectSell}"
            );

            return false;
        }

        private static bool IsBodyStrong(MqlRates candle, double minBodyRatio)
        {
            double range = candle.high - candle.low;
            if (range <= 0)
                return false;

            double body = Math.Abs(candle.close - candle.open);
            double ratio = body / range;

            return ratio >= minBodyRatio;
        }

        private static Profile GetProfile(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            // ============================
            // GOLD / METALS
            // ============================
            if (IsGold(symbol))
            {
                return new Profile
                {
                    LookbackCandles = 16,
                    RetestToleranceFactor = 0.38,
                    RejectionWickRatio = 0.42,
                    MaxOppositeWickRatio = 0.48,
                    MaxDistanceFromEma9Atr = 1.50,
                    ExhaustionMoveAtr = 2.80,
                    EmaRetestToleranceAtr = 0.28,
                    BreakBufferAtr = 0.04,
                    MinBreakBodyRatio = 0.42
                };
            }

            // ============================
            // INDICES
            // ============================
            if (IsIndex(symbol))
            {
                return new Profile
                {
                    LookbackCandles = 16,
                    RetestToleranceFactor = 0.40,
                    RejectionWickRatio = 0.40,
                    MaxOppositeWickRatio = 0.50,
                    MaxDistanceFromEma9Atr = 1.65,
                    ExhaustionMoveAtr = 3.00,
                    EmaRetestToleranceAtr = 0.30,
                    BreakBufferAtr = 0.04,
                    MinBreakBodyRatio = 0.40
                };
            }

            // ============================
            // CRYPTO
            // ============================
            if (IsCrypto(symbol))
            {
                return new Profile
                {
                    LookbackCandles = 18,
                    RetestToleranceFactor = 0.45,
                    RejectionWickRatio = 0.38,
                    MaxOppositeWickRatio = 0.52,
                    MaxDistanceFromEma9Atr = 1.85,
                    ExhaustionMoveAtr = 3.20,
                    EmaRetestToleranceAtr = 0.35,
                    BreakBufferAtr = 0.05,
                    MinBreakBodyRatio = 0.38
                };
            }

            // ============================
            // FX DEFAULT
            // ============================
            return new Profile
            {
                LookbackCandles = 18,
                RetestToleranceFactor = 0.35,
                RejectionWickRatio = 0.40,
                MaxOppositeWickRatio = 0.50,
                MaxDistanceFromEma9Atr = 1.25,
                ExhaustionMoveAtr = 2.50,
                EmaRetestToleranceAtr = 0.25,
                BreakBufferAtr = 0.035,
                MinBreakBodyRatio = 0.40
            };
        }

        private static string NormalizeSymbol(string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.ToUpperInvariant();
        }

        private static bool IsGold(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("XAU") ||
                   symbol.Contains("XAG") ||
                   symbol.Contains("GOLD") ||
                   symbol.Contains("SILVER");
        }

        private static bool IsIndex(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("USTEC") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("DJ") ||
                   symbol.Contains("SPX") ||
                   symbol.Contains("US500") ||
                   symbol.Contains("DAX") ||
                   symbol.Contains("GER") ||
                   symbol.Contains("UK100");
        }

        private static bool IsCrypto(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC") ||
                   symbol.Contains("LINK");
        }
    }
}
