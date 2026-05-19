using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    using System;
    using System.Linq;

    public static class ExhaustionReversalEngine
    {
        private class ExhaustionProfile
        {
            public int TrendLookback;
            public double MinWickAtrFactor;
            public double MaxBodyRatioForWick;
            public double MinExpansionBodyRatio;
            public double MinExpansionRangeAtr;
            public double MaxEmaSpreadAtr;
            public double MaxDistanceFromEma9Atr;
            public double SweepBufferAtr;
            public double EmaReclaimToleranceAtr;
            public double MinAtrPercentile;
        }

        public static int TrendLookback = 3;

        // Wick exhaustion
        public static double MinWickAtrFactor = 0.8;
        public static double MaxBodyRatioForWick = 0.25;

        // Early expansion reversal
        public static double MinExpansionBodyRatio = 0.65;
        public static double MinExpansionRangeAtr = 1.4;

        // Trend / context guards
        public static double MaxEmaSpreadAtrGold = 0.85;
        public static double MaxEmaSpreadAtrFx = 0.90;
        public static double MaxEmaSpreadAtrCrypto = 1.40;
        public static double MaxEmaSpreadAtrIndex = 1.20;

        public static int LondonOpen = 3;
        public static int LondonClose = 11;
        public static int NYOpen = 8;
        public static int NYClose = 17;

        public static bool ShouldEnter(
            string symbol,
            MtApi5Client client,
            double atr,
            double atrPercentile,
            double[] emaFast,
            double[] emaSlow,
            SymbolContext ctx,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (string.IsNullOrWhiteSpace(symbol) ||
                client == null ||
                ctx == null ||
                atr <= 0 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 4 || emaSlow.Length < 4)
            {
                return false;
            }

            symbol = NormalizeSymbol(symbol);

            var profile = GetProfile(symbol);

            double normalizedAtrPercentile = atrPercentile > 1.0
                ? atrPercentile / 100.0
                : atrPercentile;

            // -----------------------------------------
            // REGIME FILTER
            // Keep selective, but allow Exhaustion/LateExpansion too.
            // -----------------------------------------
            bool validState =
                ctx.State == MarketState.Armed ||
                ctx.State == MarketState.Tradable ||
                ctx.State == MarketState.Exhaustion ||
                ctx.State == MarketState.LateExpansion;

            if (!validState)
            {
                LogFile.Write($"[{symbol}] Exhaustion blocked: invalid state {ctx.State}");
                return false;
            }

            // -----------------------------------------
            // ATR REGIME
            // Avoid taking reversals in dead markets.
            // -----------------------------------------
            if (normalizedAtrPercentile < profile.MinAtrPercentile)
            {
                LogFile.Write(
                    $"[{symbol}] Exhaustion blocked: ATR percentile too low | " +
                    $"ATR%={normalizedAtrPercentile:F2}, need>={profile.MinAtrPercentile:F2}"
                );
                return false;
            }

            bool isCrypto = IsCrypto(symbol);

            // -----------------------------------------
            // SESSION FILTER
            // Crypto can run anytime.
            // Non-crypto still prefers London/NY, but allow Armed/Exhaustion state
            // slightly outside session because reversals sometimes form at session edges.
            // -----------------------------------------
            if (!isCrypto)
            {
                int hour = DateTime.UtcNow.AddHours(-4).Hour; // AST
                bool inLondon = hour >= LondonOpen && hour <= LondonClose;
                bool inNY = hour >= NYOpen && hour <= NYClose;
                bool nearSessionEdge =
                    hour == LondonOpen - 1 ||
                    hour == LondonClose + 1 ||
                    hour == NYOpen - 1 ||
                    hour == NYClose + 1;

                bool allowOutsideSession =
                    (ctx.State == MarketState.Exhaustion || ctx.State == MarketState.Armed) &&
                    nearSessionEdge;

                if (!inLondon && !inNY && !allowOutsideSession)
                {
                    LogFile.Write($"[{symbol}] Exhaustion blocked: outside London/NY session. AST hour={hour}");
                    return false;
                }
            }

            client.CopyRates(
                symbol,
                ENUM_TIMEFRAMES.PERIOD_H1,
                0,
                profile.TrendLookback + 6,
                out MqlRates[] rates);

            if (rates == null || rates.Length < profile.TrendLookback + 5)
            {
                LogFile.Write($"[{symbol}] Exhaustion blocked: insufficient H1 rates");
                return false;
            }

            // CLOSED candles only.
            // Depending on MtApi5 ordering, this matches your existing convention:
            // rates[0] is current forming, rates[1] is latest closed.
            var c0 = rates[1]; // latest closed / reversal candle
            var c1 = rates[2];
            var c2 = rates[3];

            double range0 = c0.high - c0.low;
            if (range0 <= 0)
                return false;

            double body0 = Math.Abs(c0.close - c0.open);
            double bodyRatio0 = body0 / range0;

            double lowerWick0 = Math.Min(c0.open, c0.close) - c0.low;
            double upperWick0 = c0.high - Math.Max(c0.open, c0.close);

            double lowerWickRatio = lowerWick0 / range0;
            double upperWickRatio = upperWick0 / range0;

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];
            double ema9Prev = emaFast[^3];
            double ema21Prev = emaSlow[^3];

            bool trendUp = ema9 > ema21 && ema9Prev >= ema21Prev;
            bool trendDown = ema9 < ema21 && ema9Prev <= ema21Prev;

            if (!trendUp && !trendDown)
            {
                LogFile.Write($"[{symbol}] Exhaustion blocked: no prior EMA trend");
                return false;
            }

            double emaSpread = Math.Abs(ema9 - ema21);

            // If trend is still extremely wide, avoid fading unless there is a sweep/reclaim.
            bool emaSpreadTooWide = emaSpread > atr * profile.MaxEmaSpreadAtr;

            double distanceFromEma9 = Math.Abs(c0.close - ema9);

            if (distanceFromEma9 > atr * profile.MaxDistanceFromEma9Atr)
            {
                LogFile.Write(
                    $"[{symbol}] Exhaustion blocked: reversal close too far from EMA9 | " +
                    $"dist={distanceFromEma9:F5}, max={atr * profile.MaxDistanceFromEma9Atr:F5}"
                );
                return false;
            }

            // -----------------------------------------
            // PRIOR DIRECTIONAL PRESSURE
            // More flexible than requiring every prior candle to be the same color.
            // -----------------------------------------
            int bearishCount = 0;
            int bullishCount = 0;
            double bearishRangeSum = 0;
            double bullishRangeSum = 0;

            for (int i = 2; i < 2 + profile.TrendLookback; i++)
            {
                double r = rates[i].high - rates[i].low;

                if (rates[i].close < rates[i].open)
                {
                    bearishCount++;
                    bearishRangeSum += Math.Max(r, 0);
                }

                if (rates[i].close > rates[i].open)
                {
                    bullishCount++;
                    bullishRangeSum += Math.Max(r, 0);
                }
            }

            int requiredDirectionalCandles = Math.Max(2, profile.TrendLookback - 1);

            bool priorBearPressure =
                bearishCount >= requiredDirectionalCandles ||
                c1.close < c2.close;

            bool priorBullPressure =
                bullishCount >= requiredDirectionalCandles ||
                c1.close > c2.close;

            // -----------------------------------------
            // DRIVE SLOWING / FAILURE
            // More flexible than requiring perfect slowing.
            // -----------------------------------------
            double range1 = c1.high - c1.low;

            bool bearishDriveSlowing =
                priorBearPressure &&
                (
                    range0 <= range1 * 1.15 ||
                    c0.close >= c1.close ||
                    c0.low < c1.low && c0.close > c1.low
                );

            bool bullishDriveSlowing =
                priorBullPressure &&
                (
                    range0 <= range1 * 1.15 ||
                    c0.close <= c1.close ||
                    c0.high > c1.high && c0.close < c1.high
                );

            // -----------------------------------------
            // SWEEP / REJECTION
            // -----------------------------------------
            double sweepBuffer = atr * profile.SweepBufferAtr;

            bool sweptLowAndRecovered =
                c0.low < Math.Min(c1.low, c2.low) - sweepBuffer &&
                c0.close > Math.Min(c1.close, c2.close);

            bool sweptHighAndRejected =
                c0.high > Math.Max(c1.high, c2.high) + sweepBuffer &&
                c0.close < Math.Max(c1.close, c2.close);

            bool reclaimedEma9 =
                c0.close > ema9 - atr * profile.EmaReclaimToleranceAtr;

            bool lostEma9 =
                c0.close < ema9 + atr * profile.EmaReclaimToleranceAtr;

            // -----------------------------------------
            // 1) Early expansion reversal
            // -----------------------------------------
            bool strongBullishReversal =
                trendDown &&
                bearishDriveSlowing &&
                c0.close > c0.open &&
                bodyRatio0 >= profile.MinExpansionBodyRatio &&
                range0 >= atr * profile.MinExpansionRangeAtr &&
                (
                    c0.close > c1.high ||
                    sweptLowAndRecovered
                ) &&
                reclaimedEma9;

            bool strongBearishReversal =
                trendUp &&
                bullishDriveSlowing &&
                c0.close < c0.open &&
                bodyRatio0 >= profile.MinExpansionBodyRatio &&
                range0 >= atr * profile.MinExpansionRangeAtr &&
                (
                    c0.close < c1.low ||
                    sweptHighAndRejected
                ) &&
                lostEma9;

            // If EMA spread is too wide, require sweep/reclaim before fading.
            if (emaSpreadTooWide)
            {
                strongBullishReversal =
                    strongBullishReversal &&
                    sweptLowAndRecovered &&
                    reclaimedEma9;

                strongBearishReversal =
                    strongBearishReversal &&
                    sweptHighAndRejected &&
                    lostEma9;
            }

            if (strongBullishReversal)
            {
                action = TradeAction.EnterBuy;

                LogFile.Write(
                    $"[{symbol}] Early Exhaustion BUY | " +
                    $"ATR%={normalizedAtrPercentile:F2}, rangeAtr={(range0 / atr):F2}, " +
                    $"bodyRatio={bodyRatio0:F2}, sweptLow={sweptLowAndRecovered}, " +
                    $"emaSpread={emaSpread:F5}"
                );

                return true;
            }

            if (strongBearishReversal)
            {
                action = TradeAction.EnterSell;

                LogFile.Write(
                    $"[{symbol}] Early Exhaustion SELL | " +
                    $"ATR%={normalizedAtrPercentile:F2}, rangeAtr={(range0 / atr):F2}, " +
                    $"bodyRatio={bodyRatio0:F2}, sweptHigh={sweptHighAndRejected}, " +
                    $"emaSpread={emaSpread:F5}"
                );

                return true;
            }

            // -----------------------------------------
            // 2) Classic wick exhaustion
            // -----------------------------------------
            bool wickBullishExhaustion =
                trendDown &&
                bearishDriveSlowing &&
                lowerWick0 >= atr * profile.MinWickAtrFactor &&
                bodyRatio0 <= profile.MaxBodyRatioForWick &&
                lowerWickRatio >= 0.35 &&
                (
                    c0.close >= c1.close ||
                    sweptLowAndRecovered ||
                    reclaimedEma9
                );

            bool wickBearishExhaustion =
                trendUp &&
                bullishDriveSlowing &&
                upperWick0 >= atr * profile.MinWickAtrFactor &&
                bodyRatio0 <= profile.MaxBodyRatioForWick &&
                upperWickRatio >= 0.35 &&
                (
                    c0.close <= c1.close ||
                    sweptHighAndRejected ||
                    lostEma9
                );

            // If EMA trend is still very strong, only take wick exhaustion if there was a sweep.
            if (emaSpreadTooWide)
            {
                wickBullishExhaustion =
                    wickBullishExhaustion &&
                    sweptLowAndRecovered;

                wickBearishExhaustion =
                    wickBearishExhaustion &&
                    sweptHighAndRejected;
            }

            if (wickBullishExhaustion)
            {
                action = TradeAction.EnterBuy;

                LogFile.Write(
                    $"[{symbol}] Wick Exhaustion BUY | " +
                    $"ATR%={normalizedAtrPercentile:F2}, lowerWickAtr={(lowerWick0 / atr):F2}, " +
                    $"bodyRatio={bodyRatio0:F2}, sweptLow={sweptLowAndRecovered}, " +
                    $"reclaimedEMA9={reclaimedEma9}"
                );

                return true;
            }

            if (wickBearishExhaustion)
            {
                action = TradeAction.EnterSell;

                LogFile.Write(
                    $"[{symbol}] Wick Exhaustion SELL | " +
                    $"ATR%={normalizedAtrPercentile:F2}, upperWickAtr={(upperWick0 / atr):F2}, " +
                    $"bodyRatio={bodyRatio0:F2}, sweptHigh={sweptHighAndRejected}, " +
                    $"lostEMA9={lostEma9}"
                );

                return true;
            }

            LogFile.Write(
                $"[{symbol}] Exhaustion blocked | " +
                $"state={ctx.State}, trendUp={trendUp}, trendDown={trendDown}, " +
                $"bearSlow={bearishDriveSlowing}, bullSlow={bullishDriveSlowing}, " +
                $"bodyRatio={bodyRatio0:F2}, lowerWickAtr={(lowerWick0 / atr):F2}, upperWickAtr={(upperWick0 / atr):F2}, " +
                $"sweptLow={sweptLowAndRecovered}, sweptHigh={sweptHighAndRejected}, " +
                $"emaSpreadTooWide={emaSpreadTooWide}"
            );

            return false;
        }

        private static ExhaustionProfile GetProfile(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (IsGold(symbol))
            {
                return new ExhaustionProfile
                {
                    TrendLookback = 3,
                    MinWickAtrFactor = 0.65,
                    MaxBodyRatioForWick = 0.32,
                    MinExpansionBodyRatio = 0.56,
                    MinExpansionRangeAtr = 1.10,
                    MaxEmaSpreadAtr = MaxEmaSpreadAtrGold,
                    MaxDistanceFromEma9Atr = 1.70,
                    SweepBufferAtr = 0.04,
                    EmaReclaimToleranceAtr = 0.15,
                    MinAtrPercentile = 0.35
                };
            }

            if (IsIndex(symbol))
            {
                return new ExhaustionProfile
                {
                    TrendLookback = 3,
                    MinWickAtrFactor = 0.70,
                    MaxBodyRatioForWick = 0.34,
                    MinExpansionBodyRatio = 0.58,
                    MinExpansionRangeAtr = 1.15,
                    MaxEmaSpreadAtr = MaxEmaSpreadAtrIndex,
                    MaxDistanceFromEma9Atr = 1.85,
                    SweepBufferAtr = 0.04,
                    EmaReclaimToleranceAtr = 0.16,
                    MinAtrPercentile = 0.35
                };
            }

            if (IsCrypto(symbol))
            {
                return new ExhaustionProfile
                {
                    TrendLookback = 3,
                    MinWickAtrFactor = 0.65,
                    MaxBodyRatioForWick = 0.35,
                    MinExpansionBodyRatio = 0.54,
                    MinExpansionRangeAtr = 1.05,
                    MaxEmaSpreadAtr = MaxEmaSpreadAtrCrypto,
                    MaxDistanceFromEma9Atr = 2.05,
                    SweepBufferAtr = 0.05,
                    EmaReclaimToleranceAtr = 0.18,
                    MinAtrPercentile = 0.32
                };
            }

            // FX default
            return new ExhaustionProfile
            {
                TrendLookback = 3,
                MinWickAtrFactor = 0.70,
                MaxBodyRatioForWick = 0.30,
                MinExpansionBodyRatio = 0.58,
                MinExpansionRangeAtr = 1.15,
                MaxEmaSpreadAtr = MaxEmaSpreadAtrFx,
                MaxDistanceFromEma9Atr = 1.45,
                SweepBufferAtr = 0.035,
                EmaReclaimToleranceAtr = 0.14,
                MinAtrPercentile = 0.35
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

        private static bool IsCrypto(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("LINK") ||
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC");
        }

        private static bool IsIndex(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            return symbol.Contains("US100") ||
                   symbol.Contains("NAS100") ||
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

        private static bool IsFx(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (IsGold(symbol) || IsCrypto(symbol) || IsIndex(symbol))
                return false;

            return symbol.Length >= 6 &&
                   (symbol.EndsWith("USD") ||
                    symbol.EndsWith("JPY") ||
                    symbol.EndsWith("EUR") ||
                    symbol.EndsWith("GBP") ||
                    symbol.EndsWith("AUD") ||
                    symbol.EndsWith("NZD") ||
                    symbol.EndsWith("CAD") ||
                    symbol.EndsWith("CHF"));
        }
    }
}
