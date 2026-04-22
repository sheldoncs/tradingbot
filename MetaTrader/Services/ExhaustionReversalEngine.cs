using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class ExhaustionReversalEngine
    {
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

            if (atr <= 0 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 4 || emaSlow.Length < 4)
                return false;

            if (ctx.State != MarketState.Armed &&
                ctx.State != MarketState.Tradable)
                return false;

            bool isCrypto = IsCrypto(symbol);
            bool isGold = IsGold(symbol);
            bool isIndex = IsIndex(symbol);
            bool isFx = IsFx(symbol);

            // Session filter for non-crypto
            if (!isCrypto)
            {
                int hour = DateTime.UtcNow.AddHours(-4).Hour;
                bool inLondon = hour >= LondonOpen && hour <= LondonClose;
                bool inNY = hour >= NYOpen && hour <= NYClose;

                if (!inLondon && !inNY)
                    return false;
            }

            double maxEmaSpreadAtr = isGold ? MaxEmaSpreadAtrGold
                : isFx ? MaxEmaSpreadAtrFx
                : isCrypto ? MaxEmaSpreadAtrCrypto
                : isIndex ? MaxEmaSpreadAtrIndex
                : 0.9;

            client.CopyRates(
                symbol,
                ENUM_TIMEFRAMES.PERIOD_H1,
                0,
                TrendLookback + 4,
                out MqlRates[] rates);

            if (rates == null || rates.Length < TrendLookback + 4)
                return false;

            // CLOSED candles only
            var c0 = rates[1]; // latest closed
            var c1 = rates[2];
            var c2 = rates[3];
            var c3 = rates[4];

            double range0 = c0.high - c0.low;
            if (range0 <= 0)
                return false;

            double body0 = Math.Abs(c0.close - c0.open);
            double bodyRatio0 = body0 / range0;

            double lowerWick0 = Math.Min(c0.open, c0.close) - c0.low;
            double upperWick0 = c0.high - Math.Max(c0.open, c0.close);

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];
            double ema9Prev = emaFast[^3];
            double ema21Prev = emaSlow[^3];

            bool trendUp = ema9 > ema21 && ema9Prev > ema21Prev;
            bool trendDown = ema9 < ema21 && ema9Prev < ema21Prev;

            double emaSpread = Math.Abs(ema9 - ema21);

            // If trend is still too strong, do not fade it
            if (emaSpread > atr * maxEmaSpreadAtr)
                return false;

            // Count prior directional candles
            int bearishCount = 0;
            int bullishCount = 0;

            for (int i = 2; i < 2 + TrendLookback; i++)
            {
                if (rates[i].close < rates[i].open)
                    bearishCount++;

                if (rates[i].close > rates[i].open)
                    bullishCount++;
            }

            // -----------------------------------------
            // Context: require some loss of drive first
            // -----------------------------------------
            double range1 = c1.high - c1.low;
            double range2 = c2.high - c2.low;

            bool bearishDriveSlowing =
                bearishCount >= TrendLookback &&
                range0 <= range1 * 1.05 &&
                c0.close >= c1.close; // no fresh downside follow-through

            bool bullishDriveSlowing =
                bullishCount >= TrendLookback &&
                range0 <= range1 * 1.05 &&
                c0.close <= c1.close; // no fresh upside follow-through

            // -----------------------------------------
            // 1) Early expansion reversal
            // Require actual structure break
            // -----------------------------------------
            bool strongBullishReversal =
                bearishDriveSlowing &&
                c0.close > c0.open &&
                bodyRatio0 >= MinExpansionBodyRatio &&
                range0 >= atr * MinExpansionRangeAtr &&
                c0.close > c1.high &&     // break prior candle high
                c0.close > ema9;          // reclaim fast EMA

            if (strongBullishReversal)
            {
                action = TradeAction.EnterBuy;
                LogFile.Write($"[{symbol}] Early Exhaustion BUY detected");
                return true;
            }

            bool strongBearishReversal =
                bullishDriveSlowing &&
                c0.close < c0.open &&
                bodyRatio0 >= MinExpansionBodyRatio &&
                range0 >= atr * MinExpansionRangeAtr &&
                c0.close < c1.low &&      // break prior candle low
                c0.close < ema9;          // lose fast EMA

            if (strongBearishReversal)
            {
                action = TradeAction.EnterSell;
                LogFile.Write($"[{symbol}] Early Exhaustion SELL detected");
                return true;
            }

            // -----------------------------------------
            // 2) Classic wick exhaustion
            // Only after slowing, and not against intact EMA trend
            // -----------------------------------------
            bool wickBullishExhaustion =
                bearishDriveSlowing &&
                lowerWick0 > atr * MinWickAtrFactor &&
                bodyRatio0 < MaxBodyRatioForWick &&
                c0.close >= c1.close;     // at least holding / rejecting lows

            if (wickBullishExhaustion)
            {
                action = TradeAction.EnterBuy;
                LogFile.Write($"[{symbol}] Wick Exhaustion BUY detected");
                return true;
            }

            bool wickBearishExhaustion =
                bullishDriveSlowing &&
                upperWick0 > atr * MinWickAtrFactor &&
                bodyRatio0 < MaxBodyRatioForWick &&
                c0.close <= c1.close;     // at least holding / rejecting highs

            if (wickBearishExhaustion)
            {
                action = TradeAction.EnterSell;
                LogFile.Write($"[{symbol}] Wick Exhaustion SELL detected");
                return true;
            }

            return false;
        }

        private static bool IsGold(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            symbol = symbol.ToUpperInvariant();
            return symbol.Contains("XAU");
        }

        private static bool IsCrypto(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            symbol = symbol.ToUpperInvariant();

            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("LINK");
        }

        private static bool IsIndex(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            symbol = symbol.ToUpperInvariant();

            return symbol.Contains("US100") ||
                   symbol.Contains("NAS100") ||
                   symbol.Contains("USTEC") ||
                   symbol.Contains("US30");
        }

        private static bool IsFx(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            symbol = symbol.ToUpperInvariant();

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
