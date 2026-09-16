using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;

public static class OverallMarketStructureEngine
{
    public enum MarketBias
    {
        Bullish,
        Bearish,
        Neutral,
        Choppy
    }

    public class StructureDecision
    {
        public MarketBias Bias { get; set; }
        public double Confidence { get; set; }
        public int Score { get; set; }
        public bool BuyAllowed { get; set; }
        public bool SellAllowed { get; set; }
        public bool StrongBullishOverride { get; set; }
        public bool StrongBearishOverride { get; set; }
        public bool IsChoppy { get; set; }
        public string Reason { get; set; }
    }

    public static StructureDecision Evaluate(
        string symbol,
        MqlRates[] rates,
        double[] emaFast,
        double[] emaSlow,
        double atr,
        double bid,
        double ask)
    {
        var result = new StructureDecision
        {
            Bias = MarketBias.Neutral,
            Confidence = 0,
            Score = 0,
            BuyAllowed = false,
            SellAllowed = false,
            StrongBullishOverride = false,
            StrongBearishOverride = false,
            IsChoppy = false,
            Reason = "Insufficient structure"
        };

        try
        {
            if (rates == null || rates.Length < 8 ||
                emaFast == null || emaFast.Length < 3 ||
                emaSlow == null || emaSlow.Length < 3 ||
                atr <= 0 || bid <= 0 || ask <= 0)
            {
                return result;
            }

            string s = symbol?.ToUpperInvariant() ?? "";
            double mid = (bid + ask) / 2.0;

            var c0 = rates[^1];
            var c1 = rates[^2];
            var c2 = rates[^3];
            var c3 = rates[^4];
            var c4 = rates[^5];

            // CLOSED EMA values supplied by caller.
            double closedEma9 = emaFast[^1];
            double closedEma21 = emaSlow[^1];

            // LIVE EMA estimates -- same formula used by FastEntryEngine.
            double ema9 = CalculateLiveEma(closedEma9, mid, 9);
            double ema21 = CalculateLiveEma(closedEma21, mid, 21);

            bool emaBullAligned = ema9 > ema21;
            bool emaBearAligned = ema9 < ema21;

            // LIVE slope: compare live EMA with latest closed EMA.
            bool ema9Rising = ema9 > closedEma9;
            bool ema9Falling = ema9 < closedEma9;

            bool ema21Rising = ema21 > closedEma21;
            bool ema21Falling = ema21 < closedEma21;

            bool priceAboveBoth = mid > ema9 && mid > ema21;
            bool priceBelowBoth = mid < ema9 && mid < ema21;

            double emaSpreadAtr = Math.Abs(ema9 - ema21) / atr;
            double priceToEma9Atr = Math.Abs(mid - ema9) / atr;

            double recentHighA = Math.Max(c0.high, c1.high);
            double recentHighB = Math.Max(c2.high, c3.high);
            double recentLowA = Math.Min(c0.low, c1.low);
            double recentLowB = Math.Min(c2.low, c3.low);

            bool higherHigh = recentHighA > recentHighB;
            bool higherLow = recentLowA > recentLowB;
            bool lowerHigh = recentHighA < recentHighB;
            bool lowerLow = recentLowA < recentLowB;

            bool bullishStructure = higherHigh && higherLow;
            bool bearishStructure = lowerHigh && lowerLow;

            double priorStructureHigh = new[] { c1.high, c2.high, c3.high, c4.high }.Max();
            double priorStructureLow = new[] { c1.low, c2.low, c3.low, c4.low }.Min();

            bool bullishBreakout = mid > priorStructureHigh;
            bool bearishBreakdown = mid < priorStructureLow;

            // rates[^1] is the latest CLOSED H1 candle.
            // FastEntryEngine uses its close as the synthetic live H1 open.
            double liveOpen = c0.close;

            double liveBullBodyAtr = Math.Max(0.0, mid - liveOpen) / atr;
            double liveBearBodyAtr = Math.Max(0.0, liveOpen - mid) / atr;

            double strongMoveThreshold = GetStrongMoveThreshold(s);

            bool strongBullishLiveMove = liveBullBodyAtr >= strongMoveThreshold;
            bool strongBearishLiveMove = liveBearBodyAtr >= strongMoveThreshold;

            int directionChanges = 0;
            MqlRates[] recentCandles = { c0, c1, c2, c3, c4 };

            for (int i = 0; i < recentCandles.Length - 1; i++)
            {
                bool currentBull = recentCandles[i].close > recentCandles[i].open;
                bool previousBull = recentCandles[i + 1].close > recentCandles[i + 1].open;

                if (currentBull != previousBull)
                    directionChanges++;
            }

            bool compressedEmas = emaSpreadAtr < GetCompressionSpreadThreshold(s);
            bool nearEmaZone = priceToEma9Atr < 0.50;
            bool excessiveAlternation = directionChanges >= 3;

            bool isChoppy = compressedEmas && nearEmaZone && excessiveAlternation;

            int score = 0;

            if (emaBullAligned) score += 2;
            if (emaBearAligned) score -= 2;
            if (ema9Rising) score += 2;
            if (ema9Falling) score -= 2;
            if (ema21Rising) score += 1;
            if (ema21Falling) score -= 1;
            if (priceAboveBoth) score += 2;
            if (priceBelowBoth) score -= 2;
            if (bullishStructure) score += 2;
            if (bearishStructure) score -= 2;
            if (bullishBreakout) score += 2;
            if (bearishBreakdown) score -= 2;
            if (strongBullishLiveMove) score += 2;
            if (strongBearishLiveMove) score -= 2;

            bool strongBullishOverride =
                strongBullishLiveMove &&
                priceAboveBoth &&
                ema9Rising;

            bool strongBearishOverride =
                strongBearishLiveMove &&
                priceBelowBoth &&
                ema9Falling;

            MarketBias bias;

            if (isChoppy && !strongBullishOverride && !strongBearishOverride)
                bias = MarketBias.Choppy;
            else if (score >= 5 || strongBullishOverride)
                bias = MarketBias.Bullish;
            else if (score <= -5 || strongBearishOverride)
                bias = MarketBias.Bearish;
            else
                bias = MarketBias.Neutral;

            double confidence = Math.Min(100.0, Math.Abs(score) / 12.0 * 100.0);

            result.Bias = bias;
            result.Confidence = confidence;
            result.Score = score;
            result.BuyAllowed = bias == MarketBias.Bullish;
            result.SellAllowed = bias == MarketBias.Bearish;
            result.StrongBullishOverride = strongBullishOverride;
            result.StrongBearishOverride = strongBearishOverride;
            result.IsChoppy = isChoppy;
            result.Reason =
                $"bias={bias} | score={score} | confidence={confidence:F0}% | " +
                $"emaBull={emaBullAligned} | emaBear={emaBearAligned} | " +
                $"closedEma9={closedEma9:F5} | liveEma9={ema9:F5} | " +
                $"closedEma21={closedEma21:F5} | liveEma21={ema21:F5} | " +
                $"liveOpen={liveOpen:F5} | " +
                $"ema9Rising={ema9Rising} | ema9Falling={ema9Falling} | " +
                $"ema21Rising={ema21Rising} | ema21Falling={ema21Falling} | " +
                $"priceAboveBoth={priceAboveBoth} | priceBelowBoth={priceBelowBoth} | " +
                $"HH={higherHigh} | HL={higherLow} | LH={lowerHigh} | LL={lowerLow} | " +
                $"bullBreak={bullishBreakout} | bearBreak={bearishBreakdown} | " +
                $"bullLiveATR={liveBullBodyAtr:F2} | bearLiveATR={liveBearBodyAtr:F2} | " +
                $"emaSpreadATR={emaSpreadAtr:F2} | directionChanges={directionChanges} | " +
                $"choppy={isChoppy} | bullOverride={strongBullishOverride} | bearOverride={strongBearishOverride}";

            LogFile.Write($"[{symbol}] 🧭 OVERALL STRUCTURE | {result.Reason}");

            return result;
        }
        catch (Exception ex)
        {
            LogFile.Write($"[{symbol}] OverallMarketStructureEngine error: {ex.Message}");
            return result;
        }
    }

    private static double CalculateLiveEma(
        double previousClosedEma,
        double livePrice,
        int period)
    {
        if (previousClosedEma <= 0 || livePrice <= 0 || period <= 0)
            return previousClosedEma;

        double alpha = 2.0 / (period + 1.0);

        return previousClosedEma + alpha * (livePrice - previousClosedEma);
    }

    private static double GetStrongMoveThreshold(string symbol)
    {
        if (IsGold(symbol)) return 1.20;
        if (IsCrypto(symbol)) return 1.25;
        if (IsIndex(symbol)) return 1.15;
        if (IsFx(symbol)) return 0.85;
        return 1.10;
    }

    private static double GetCompressionSpreadThreshold(string symbol)
    {
        if (IsGold(symbol)) return 0.22;
        if (IsCrypto(symbol)) return 0.20;
        if (IsIndex(symbol)) return 0.22;
        if (IsFx(symbol)) return 0.18;
        return 0.20;
    }

    private static bool IsGold(string symbol)
    {
        return symbol.Contains("XAU") || symbol.Contains("XAG") ||
               symbol.Contains("GOLD") || symbol.Contains("SILVER");
    }

    private static bool IsCrypto(string symbol)
    {
        return symbol.Contains("BTC") || symbol.Contains("ETH") ||
               symbol.Contains("SOL") || symbol.Contains("BNB") ||
               symbol.Contains("XRP") || symbol.Contains("ADA") ||
               symbol.Contains("DOGE") || symbol.Contains("LTC") ||
               symbol.Contains("AVAX") || symbol.Contains("LINK");
    }

    private static bool IsIndex(string symbol)
    {
        return symbol.Contains("US100") || symbol.Contains("NAS") ||
               symbol.Contains("USTEC") || symbol.Contains("US30") ||
               symbol.Contains("DJ") || symbol.Contains("US500") ||
               symbol.Contains("SPX") || symbol.Contains("GER") ||
               symbol.Contains("DAX") || symbol.Contains("UK100");
    }

    private static bool IsFx(string symbol)
    {
        if (symbol.Length < 6)
            return false;

        string[] currencies =
        {
            "USD", "EUR", "GBP", "JPY", "AUD", "NZD", "CAD", "CHF"
        };

        foreach (var baseCurrency in currencies)
        {
            foreach (var quoteCurrency in currencies)
            {
                if (baseCurrency == quoteCurrency)
                    continue;

                if (symbol.StartsWith(baseCurrency) && symbol.Contains(quoteCurrency))
                    return true;
            }
        }

        return false;
    }
}

