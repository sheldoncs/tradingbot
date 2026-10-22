namespace MetaTrader.Services.Helper
{
    using global::MetaTrader.Enums;
    using global::MetaTrader.Util;
    using MtApi5;
    using System;
    using System.Linq;

    public static class TrendContinuationEntryEngine
    {
        // ================================================================
        // PROFILE
        // ================================================================

        private class ContinuationProfile
        {
            public double MinMomentum { get; set; }

            // Minimum body of the LIVE continuation candle
            public double MinBodyAtr { get; set; }

            // Previous pullback candle must reach reasonably close to EMA9
            public double PullbackToEma9Atr { get; set; }

            // Allow pullback to move slightly through EMA21
            public double MaxBeyondEma21Atr { get; set; }

            // LIVE continuation candle must break previous candle high/low
            // by at least this amount.
            public double BreakBufferAtr { get; set; }

            // Do not enter if continuation is already too stretched
            // from EMA9.
            public double MaxEntryDistanceFromEma9Atr { get; set; }

            // Prevent very tiny pullback candles from being interpreted
            // as a meaningful reset.
            public double MinPullbackBodyAtr { get; set; }

            // Prevent huge pullback candles from qualifying.
            public double MaxPullbackBodyAtr { get; set; }
        }


        // Observation-only Gold continuation diagnostics. This state controls
        // logging frequency, never trading decisions.
        private static readonly object _goldEarlyDiagnosticSync = new object();
        private static readonly System.Collections.Generic.Dictionary<string, System.DateTime>
            _goldEarlyDiagnosticMinute = new System.Collections.Generic.Dictionary<string, System.DateTime>();

        private static bool ShouldLogGoldEarlyDiagnostic(string symbol, string direction)
        {
            var now = System.DateTime.UtcNow;
            var minute = new System.DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, System.DateTimeKind.Utc);
            var key = symbol.ToUpperInvariant() + ":" + direction;
            lock (_goldEarlyDiagnosticSync)
            {
                if (_goldEarlyDiagnosticMinute.TryGetValue(key, out var last) && last == minute)
                    return false;
                _goldEarlyDiagnosticMinute[key] = minute;
                return true;
            }
        }

        // ================================================================
        // MAIN ENTRY METHOD
        // ================================================================

        // Compatibility overload for any existing callers.
        public static bool TryEnter(
            string symbol,
            MqlRates liveCandle,
            MqlRates previousClosedCandle,
            double atr,
            double ema9,
            double ema9Previous,
            double ema21,
            double momentum,
            double bid,
            double ask,
            out TradeAction action)
        {
            return TryEnter(
                symbol,
                liveCandle,
                previousClosedCandle,
                new[] { previousClosedCandle },
                atr,
                ema9,
                ema9Previous,
                ema21,
                momentum,
                bid,
                ask,
                out action
            );
        }

        // Multi-candle continuation overload.
        // recentClosedCandles must contain CLOSED candles in chronological order.
        public static bool TryEnter(
            string symbol,
            MqlRates liveCandle,
            MqlRates previousClosedCandle,
            MqlRates[] recentClosedCandles,
            double atr,
            double ema9,
            double ema9Previous,
            double ema21,
            double momentum,
            double bid,
            double ask,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            try
            {
                if (string.IsNullOrWhiteSpace(symbol))
                    return false;

                if (atr <= 0)
                    return false;


                ContinuationProfile profile =
                    GetProfile(symbol);


                if (profile == null)
                    return false;


                // ========================================================
                // CURRENT PRICE
                // ========================================================

                double mid =
                    (bid + ask) / 2.0;


                // ========================================================
                // LIVE CANDLE DIRECTION / BODY
                // ========================================================

                bool bullishLiveCandle =
                    mid > liveCandle.open;

                bool bearishLiveCandle =
                    mid < liveCandle.open;


                double liveBody =
                    Math.Abs(
                        mid - liveCandle.open
                    );

                double liveBodyAtr =
                    liveBody / atr;


                // ========================================================
                // PREVIOUS CLOSED CANDLE
                // ========================================================

                bool previousBullish =
                    previousClosedCandle.close >
                    previousClosedCandle.open;

                bool previousBearish =
                    previousClosedCandle.close <
                    previousClosedCandle.open;


                // ========================================================
                // EMA REGIME
                // ========================================================

                bool overallBullish =
                    ema9 > ema21;

                bool overallBearish =
                    ema9 < ema21;


                bool ema9Rising =
                    ema9 > ema9Previous;

                bool ema9Falling =
                    ema9 < ema9Previous;


                // ========================================================
                // PRICE POSITION
                // ========================================================

                bool priceAboveEma9 =
                    mid > ema9;

                bool priceBelowEma9 =
                    mid < ema9;

                bool priceAboveEma21 =
                    mid > ema21;

                bool priceBelowEma21 =
                    mid < ema21;


                // ========================================================
                // DISTANCE FROM EMA9
                // ========================================================

                double distanceFromEma9Atr =
                    Math.Abs(
                        mid - ema9
                    ) / atr;


                // ========================================================
                // MOMENTUM
                // ========================================================

                bool momentumValid =
                    momentum >=
                    profile.MinMomentum;


                // ========================================================
                // RECENT 1-3 CLOSED-CANDLE RESET / PULLBACK
                //
                // PURPOSE:
                // The old version required the immediately previous candle
                // to be the entire pullback. That was too narrow for Gold:
                //
                // established trend -> 1-3 candle pause/reset -> resumption
                //
                // This version accepts a controlled reset during the most
                // recent 1-3 CLOSED candles, while keeping the existing:
                //
                // - EMA trend regime
                // - EMA9 slope
                // - EMA21 depth protection
                // - momentum requirement
                // - live continuation body requirement
                // - anti-chase distance
                // - breakout confirmation
                //
                // It does NOT create reversal entries.
                // ========================================================

                // Select the SHORTEST valid contiguous suffix (1, 2 or 3
                // most-recent CLOSED candles). Older trend impulse candles
                // must not invalidate a newer, genuine pause/pullback.
                // No candle is skipped between the reset and live candle.
                MqlRates[] source =
                    recentClosedCandles != null && recentClosedCandles.Length > 0
                        ? recentClosedCandles
                        : new[] { previousClosedCandle };

                double pauseMinBodyAtr =
                    Math.Max(0.02, profile.MinPullbackBodyAtr * 0.50);
                // A pause is SMALL, not simply any candle below the maximum
                // pullback size. This prevents ordinary trend candles from
                // masquerading as resets.
                double pauseMaxBodyAtr =
                    Math.Min(0.22, profile.MaxPullbackBodyAtr);

                bool buyPullbackSeen = false;
                bool sellPullbackSeen = false;
                int buyResetCandles = 0;
                int sellResetCandles = 0;
                double buyResetHigh = 0.0, buyResetLow = 0.0;
                double sellResetHigh = 0.0, sellResetLow = 0.0;
                bool buyBreakConfirmed = false;
                bool sellBreakConfirmed = false;
                double buyBreakLevel = double.MaxValue;
                double sellBreakLevel = double.MinValue;

                // Retain diagnostic fields for the selected (or latest)
                // candidate window, so existing log readers keep working.
                int diagnosticCount = 1;
                double resetHigh = source[source.Length - 1].high;
                double resetLow = source[source.Length - 1].low;
                bool hasBearishResetCandle = false;
                bool hasBullishResetCandle = false;
                bool hasControlledPauseCandle = false;
                bool resetBodyValid = false;
                bool hasMeaningfulResetBody = false;
                bool buyPullbackTouchedEmaZone = false;
                bool buyPullbackNotTooDeep = false;
                bool sellPullbackTouchedEmaZone = false;
                bool sellPullbackNotTooDeep = false;
                double buyPullbackDistanceToEma9 = 0.0;
                double sellPullbackDistanceToEma9 = 0.0;

                int maxCount = Math.Min(3, source.Length);
                for (int count = 1; count <= maxCount; count++)
                {
                    MqlRates[] window = source
                        .Skip(source.Length - count)
                        .Take(count)
                        .ToArray();

                    double high = window.Max(c => c.high);
                    double low = window.Min(c => c.low);
                    bool bearReset = window.Any(c => c.close < c.open);
                    bool bullReset = window.Any(c => c.close > c.open);
                    bool pauseReset = window.Any(c =>
                    {
                        double body = Math.Abs(c.close - c.open) / atr;
                        return body >= pauseMinBodyAtr &&
                               body <= pauseMaxBodyAtr;
                    });
                    bool bodyValid = window.All(c =>
                        Math.Abs(c.close - c.open) / atr <=
                        profile.MaxPullbackBodyAtr);
                    bool meaningful = window.Any(c =>
                        Math.Abs(c.close - c.open) / atr >=
                        pauseMinBodyAtr);

                    bool buyTouch = low <= ema9Previous +
                        profile.PullbackToEma9Atr * atr;
                    bool buyDepth = low >= ema21 -
                        profile.MaxBeyondEma21Atr * atr;
                    bool sellTouch = high >= ema9Previous -
                        profile.PullbackToEma9Atr * atr;
                    bool sellDepth = high <= ema21 +
                        profile.MaxBeyondEma21Atr * atr;

                    // Keep the most-recent candidate for diagnostics until a
                    // valid reset is found; never widen a successful reset.
                    if (count == 1 || (!buyPullbackSeen && !sellPullbackSeen))
                    {
                        diagnosticCount = count;
                        resetHigh = high;
                        resetLow = low;
                        hasBearishResetCandle = bearReset;
                        hasBullishResetCandle = bullReset;
                        hasControlledPauseCandle = pauseReset;
                        resetBodyValid = bodyValid;
                        hasMeaningfulResetBody = meaningful;
                        buyPullbackTouchedEmaZone = buyTouch;
                        buyPullbackNotTooDeep = buyDepth;
                        sellPullbackTouchedEmaZone = sellTouch;
                        sellPullbackNotTooDeep = sellDepth;
                        buyPullbackDistanceToEma9 =
                            Math.Abs(low - ema9Previous) / atr;
                        sellPullbackDistanceToEma9 =
                            Math.Abs(high - ema9Previous) / atr;
                    }

                    if (!buyPullbackSeen && overallBullish && ema9Rising &&
                        bodyValid && meaningful && (bearReset || pauseReset) &&
                        buyTouch && buyDepth)
                    {
                        buyPullbackSeen = true;
                        buyResetCandles = count;
                        buyResetHigh = high;
                        buyResetLow = low;
                        buyBreakLevel = high + profile.BreakBufferAtr * atr;
                        buyBreakConfirmed = mid >= buyBreakLevel;
                    }

                    if (!sellPullbackSeen && overallBearish && ema9Falling &&
                        bodyValid && meaningful && (bullReset || pauseReset) &&
                        sellTouch && sellDepth)
                    {
                        sellPullbackSeen = true;
                        sellResetCandles = count;
                        sellResetHigh = high;
                        sellResetLow = low;
                        sellBreakLevel = low - profile.BreakBufferAtr * atr;
                        sellBreakConfirmed = mid <= sellBreakLevel;
                    }

                    // Only one EMA regime can be active at a time.
                    if (buyPullbackSeen || sellPullbackSeen)
                    {
                        diagnosticCount = count;
                        resetHigh = high;
                        resetLow = low;
                        hasBearishResetCandle = bearReset;
                        hasBullishResetCandle = bullReset;
                        hasControlledPauseCandle = pauseReset;
                        resetBodyValid = bodyValid;
                        hasMeaningfulResetBody = meaningful;
                        buyPullbackTouchedEmaZone = buyTouch;
                        buyPullbackNotTooDeep = buyDepth;
                        sellPullbackTouchedEmaZone = sellTouch;
                        sellPullbackNotTooDeep = sellDepth;
                        buyPullbackDistanceToEma9 =
                            Math.Abs(low - ema9Previous) / atr;
                        sellPullbackDistanceToEma9 =
                            Math.Abs(high - ema9Previous) / atr;
                        break;
                    }
                }

                // Preserve previous-candle values for existing diagnostics.
                double previousBody =
                    Math.Abs(
                        previousClosedCandle.close -
                        previousClosedCandle.open
                    );

                double previousBodyAtr =
                    previousBody / atr;


                // Breakout levels are calculated from the selected reset
                // window above, not an unrelated older candle's high/low.

                // ========================================================
                // ANTI-CHASE
                // ========================================================

                bool entryDistanceValid =
                    distanceFromEma9Atr <=
                    profile.MaxEntryDistanceFromEma9Atr;


                // ========================================================
                // GOLD EARLY-CONTINUATION ENTRY PATH
                // ========================================================
                // Hypothesis: a smaller live body / shallower structural break
                // may appear before the normal 0.55 ATR body + 0.05 ATR break.
                // This is now a functional Gold-only early continuation signal.
                // It preserves the validated reset and EMA9 anti-chase rule.
                // Uses the SAME validated 1-3 closed-bar reset as the real path.
                if (IsGold(symbol))
                {
                    const double studyBodyAtr = 0.30;
                    const double studyMomentum = 0.40;
                    // Strictly preserve the production 1.20 ATR location limit.
                    bool studyBuyBreak = buyPullbackSeen && mid >= buyResetHigh;
                    bool studySellBreak = sellPullbackSeen && mid <= sellResetLow;

                    bool studyBuy = buyPullbackSeen && bullishLiveCandle &&
                        priceAboveEma9 && priceAboveEma21 && studyBuyBreak &&
                        liveBodyAtr >= studyBodyAtr && momentum >= studyMomentum &&
                        entryDistanceValid;

                    bool studySell = sellPullbackSeen && bearishLiveCandle &&
                        priceBelowEma9 && priceBelowEma21 && studySellBreak &&
                        liveBodyAtr >= studyBodyAtr && momentum >= studyMomentum &&
                        entryDistanceValid;

                    // Include near-candidates to diagnose which requirement is
                    // delaying recognition. One log per symbol/direction/minute.
                    if ((buyPullbackSeen || studyBuy) &&
                        ShouldLogGoldEarlyDiagnostic(symbol, "BUY"))
                    {
                        LogFile.Write(
                            $"[{symbol}] GOLD EARLY CONTINUATION STUDY | direction=BUY | " +
                            $"shadowCandidate={studyBuy} | reset={buyPullbackSeen} | " +
                            $"resetCandles={buyResetCandles} | liveDirectional={bullishLiveCandle} | " +
                            $"aboveEMA9={priceAboveEma9} | aboveEMA21={priceAboveEma21} | " +
                            $"price={mid:F5} | earlyBreak={studyBuyBreak} | productionBreak={buyBreakConfirmed} | " +
                            $"breakLevelEarly={buyResetHigh:F5} | breakLevelProduction={buyBreakLevel:F5} | " +
                            $"bodyATR={liveBodyAtr:F2} | earlyBodyReq={studyBodyAtr:F2} | productionBodyReq={profile.MinBodyAtr:F2} | " +
                            $"momentum={momentum:F2} | earlyMomentumReq={studyMomentum:F2} | productionMomentumReq={profile.MinMomentum:F2} | " +
                            $"ema9DistanceATR={distanceFromEma9Atr:F2} | maxDistanceATR={profile.MaxEntryDistanceFromEma9Atr:F2} | " +
                            $"locationValid={entryDistanceValid} | executionChanged=EarlyContinuationCandidate");
                    }

                    if ((sellPullbackSeen || studySell) &&
                        ShouldLogGoldEarlyDiagnostic(symbol, "SELL"))
                    {
                        LogFile.Write(
                            $"[{symbol}] GOLD EARLY CONTINUATION STUDY | direction=SELL | " +
                            $"shadowCandidate={studySell} | reset={sellPullbackSeen} | " +
                            $"resetCandles={sellResetCandles} | liveDirectional={bearishLiveCandle} | " +
                            $"belowEMA9={priceBelowEma9} | belowEMA21={priceBelowEma21} | " +
                            $"price={mid:F5} | earlyBreak={studySellBreak} | productionBreak={sellBreakConfirmed} | " +
                            $"breakLevelEarly={sellResetLow:F5} | breakLevelProduction={sellBreakLevel:F5} | " +
                            $"bodyATR={liveBodyAtr:F2} | earlyBodyReq={studyBodyAtr:F2} | productionBodyReq={profile.MinBodyAtr:F2} | " +
                            $"momentum={momentum:F2} | earlyMomentumReq={studyMomentum:F2} | productionMomentumReq={profile.MinMomentum:F2} | " +
                            $"ema9DistanceATR={distanceFromEma9Atr:F2} | maxDistanceATR={profile.MaxEntryDistanceFromEma9Atr:F2} | " +
                            $"locationValid={entryDistanceValid} | executionChanged=EarlyContinuationCandidate");
                    }

                    // Functional early continuation, using the already-validated
                    // shortest 1-3 closed-candle reset and the existing EMA9
                    // anti-chase ceiling. This is trend continuation, NOT reversal.
                    // Downstream FastEntry / S-R / M15 / trade-risk gates still apply.
                    if (studyBuy)
                    {
                        action = TradeAction.EnterBuy;
                        LogFile.Write($"[{symbol}] GOLD EARLY CONTINUATION BUY | " +
                            $"bodyATR={liveBodyAtr:F2} | momentum={momentum:F2} | " +
                            $"resetCandles={buyResetCandles} | breakLevel={buyResetHigh:F5} | " +
                            $"emaDistATR={distanceFromEma9Atr:F2} | executionChanged=True");
                        return true;
                    }
                    if (studySell)
                    {
                        action = TradeAction.EnterSell;
                        LogFile.Write($"[{symbol}] GOLD EARLY CONTINUATION SELL | " +
                            $"bodyATR={liveBodyAtr:F2} | momentum={momentum:F2} | " +
                            $"resetCandles={sellResetCandles} | breakLevel={sellResetLow:F5} | " +
                            $"emaDistATR={distanceFromEma9Atr:F2} | executionChanged=True");
                        return true;
                    }
                }

                // ========================================================
                // BUY CONTINUATION
                // ========================================================

                bool continuationBuy =
                    buyPullbackSeen &&

                    bullishLiveCandle &&

                    priceAboveEma9 &&
                    priceAboveEma21 &&

                    buyBreakConfirmed &&

                    liveBodyAtr >=
                        profile.MinBodyAtr &&

                    momentumValid &&

                    entryDistanceValid;


                if (continuationBuy)
                {
                    action =
                        TradeAction.EnterBuy;


                    LogFile.Write(
                        $"[{symbol}] 🔄 TREND CONTINUATION BUY | " +

                        $"momentum={momentum:F2} | " +
                        $"requiredMomentum={profile.MinMomentum:F2} | " +

                        $"bodyATR={liveBodyAtr:F2} | " +
                        $"requiredBody={profile.MinBodyAtr:F2} | " +

                        $"pullbackBodyATR={previousBodyAtr:F2} | " +
                        $"pullbackToEMA9ATR={buyPullbackDistanceToEma9:F2} | " +

                        $"emaDist={distanceFromEma9Atr:F2} | " +
                        $"maxEmaDist={profile.MaxEntryDistanceFromEma9Atr:F2} | " +

                        $"breakLevel={buyBreakLevel:F5} | " +

                        $"ema9={ema9:F5} | " +
                        $"ema21={ema21:F5} | " +

                        $"resetCandles={buyResetCandles} | resetHigh={buyResetHigh:F5} | resetLow={buyResetLow:F5} | NOTE=Pullback completed and bullish trend resumed"
                    );


                    return true;
                }


                // ========================================================
                // SELL CONTINUATION
                // ========================================================

                bool continuationSell =
                    sellPullbackSeen &&

                    bearishLiveCandle &&

                    priceBelowEma9 &&
                    priceBelowEma21 &&

                    sellBreakConfirmed &&

                    liveBodyAtr >=
                        profile.MinBodyAtr &&

                    momentumValid &&

                    entryDistanceValid;


                if (continuationSell)
                {
                    action =
                        TradeAction.EnterSell;


                    LogFile.Write(
                        $"[{symbol}] 🔄 TREND CONTINUATION SELL | " +

                        $"momentum={momentum:F2} | " +
                        $"requiredMomentum={profile.MinMomentum:F2} | " +

                        $"bodyATR={liveBodyAtr:F2} | " +
                        $"requiredBody={profile.MinBodyAtr:F2} | " +

                        $"pullbackBodyATR={previousBodyAtr:F2} | " +
                        $"pullbackToEMA9ATR={sellPullbackDistanceToEma9:F2} | " +

                        $"emaDist={distanceFromEma9Atr:F2} | " +
                        $"maxEmaDist={profile.MaxEntryDistanceFromEma9Atr:F2} | " +

                        $"breakLevel={sellBreakLevel:F5} | " +

                        $"ema9={ema9:F5} | " +
                        $"ema21={ema21:F5} | " +

                        $"resetCandles={sellResetCandles} | resetHigh={sellResetHigh:F5} | resetLow={sellResetLow:F5} | NOTE=Pullback completed and bearish trend resumed"
                    );


                    return true;
                }


                // ========================================================
                // DIAGNOSTIC LOG
                // ========================================================

                LogFile.Write(
                    $"[{symbol}] TREND CONTINUATION RESET DIAGNOSTIC | " +
                    $"resetCandles={diagnosticCount} | " +
                    $"resetHigh={resetHigh:F5} | " +
                    $"resetLow={resetLow:F5} | " +
                    $"bearReset={hasBearishResetCandle} | " +
                    $"bullReset={hasBullishResetCandle} | " +
                    $"pauseReset={hasControlledPauseCandle} | " +
                    $"resetBodyValid={resetBodyValid} | " +
                    $"meaningfulReset={hasMeaningfulResetBody} | " +
                    $"buyTouchEMA9={buyPullbackTouchedEmaZone} | " +
                    $"buyNotTooDeep={buyPullbackNotTooDeep} | " +
                    $"sellTouchEMA9={sellPullbackTouchedEmaZone} | " +
                    $"sellNotTooDeep={sellPullbackNotTooDeep} | " +
                    $"buyPullback={buyPullbackSeen} | " +
                    $"sellPullback={sellPullbackSeen} | " +
                    $"momentum={momentum:F2}/{profile.MinMomentum:F2} | " +
                    $"bodyATR={liveBodyAtr:F2}/{profile.MinBodyAtr:F2} | " +
                    $"emaDist={distanceFromEma9Atr:F2}/{profile.MaxEntryDistanceFromEma9Atr:F2}"
                );

                if (buyPullbackSeen ||
                    sellPullbackSeen)
                {
                    LogFile.Write(
                        $"[{symbol}] TREND CONTINUATION WAIT | " +

                        $"buyPullback={buyPullbackSeen} | " +
                        $"sellPullback={sellPullbackSeen} | " +

                        $"bullLive={bullishLiveCandle} | " +
                        $"bearLive={bearishLiveCandle} | " +

                        $"buyBreak={buyBreakConfirmed} | " +
                        $"sellBreak={sellBreakConfirmed} | " +

                        $"momentum={momentum:F2} | " +
                        $"momentumReq={profile.MinMomentum:F2} | " +

                        $"bodyATR={liveBodyAtr:F2} | " +
                        $"bodyReq={profile.MinBodyAtr:F2} | " +

                        $"emaDist={distanceFromEma9Atr:F2} | " +
                        $"maxEmaDist={profile.MaxEntryDistanceFromEma9Atr:F2}"
                    );
                }


                return false;
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"[{symbol}] TrendContinuationEntryEngine ERROR | " +
                    $"{ex.Message}"
                );

                action =
                    TradeAction.Hold;

                return false;
            }
        }


        // ================================================================
        // ASSET PROFILES
        // ================================================================

        private static ContinuationProfile GetProfile(
            string symbol)
        {
            if (IsGold(symbol))
            {
                return new ContinuationProfile
                {
                    MinMomentum = 0.45,

                    MinBodyAtr = 0.40,

                    PullbackToEma9Atr = 0.35,

                    MaxBeyondEma21Atr = 0.12,

                    BreakBufferAtr = 0.02,

                    MaxEntryDistanceFromEma9Atr = 1.20,

                    MinPullbackBodyAtr = 0.08,

                    MaxPullbackBodyAtr = 0.80
                };
            }


            if (IsFx(symbol))
            {
                return new ContinuationProfile
                {
                    MinMomentum = 0.40,

                    MinBodyAtr = 0.45,

                    PullbackToEma9Atr = 0.25,

                    MaxBeyondEma21Atr = 0.10,

                    BreakBufferAtr = 0.04,

                    MaxEntryDistanceFromEma9Atr = 1.20,

                    MinPullbackBodyAtr = 0.06,

                    MaxPullbackBodyAtr = 0.75
                };
            }


            if (IsCrypto(symbol))
            {
                return new ContinuationProfile
                {
                    MinMomentum = 0.50,

                    MinBodyAtr = 0.60,

                    PullbackToEma9Atr = 0.35,

                    MaxBeyondEma21Atr = 0.15,

                    BreakBufferAtr = 0.06,

                    MaxEntryDistanceFromEma9Atr = 1.35,

                    MinPullbackBodyAtr = 0.08,

                    MaxPullbackBodyAtr = 0.90
                };
            }


            if (IsIndex(symbol))
            {
                return new ContinuationProfile
                {
                    MinMomentum = 0.45,

                    MinBodyAtr = 0.55,

                    PullbackToEma9Atr = 0.30,

                    MaxBeyondEma21Atr = 0.15,

                    BreakBufferAtr = 0.05,

                    MaxEntryDistanceFromEma9Atr = 1.30,

                    MinPullbackBodyAtr = 0.08,

                    MaxPullbackBodyAtr = 0.85
                };
            }


            return null;
        }


        // ================================================================
        // SYMBOL CLASSIFICATION
        // ================================================================

        private static bool IsGold(
            string symbol)
        {
            string s =
                symbol.ToUpperInvariant();


            return
                s.Contains("XAUUSD") ||
                s.Contains("XAUEUR") ||
                s.StartsWith("XAU");
        }


        private static bool IsCrypto(
            string symbol)
        {
            string s =
                symbol.ToUpperInvariant();


            return
                s.Contains("BTC") ||
                s.Contains("ETH") ||
                s.Contains("SOL") ||
                s.Contains("BNB") ||
                s.Contains("XRP") ||
                s.Contains("ADA");
        }


        private static bool IsIndex(
            string symbol)
        {
            string s =
                symbol.ToUpperInvariant();


            return
                s.Contains("US100") ||
                s.Contains("NAS100") ||
                s.Contains("USTEC") ||
                s.Contains("US30") ||
                s.Contains("DJ30") ||
                s.Contains("SPX500") ||
                s.Contains("US500") ||
                s.Contains("GER40") ||
                s.Contains("DE40") ||
                s.Contains("UK100") ||
                s.Contains("JP225") ||
                s.Contains(".CASH");
        }


        private static bool IsFx(
            string symbol)
        {
            string s =
                symbol.ToUpperInvariant();


            if (IsGold(s) ||
                IsCrypto(s) ||
                IsIndex(s))
            {
                return false;
            }


            return
                s.Contains("EURUSD") ||
                s.Contains("GBPUSD") ||
                s.Contains("USDJPY") ||
                s.Contains("EURJPY") ||
                s.Contains("USDCAD") ||
                s.Contains("AUDUSD") ||
                s.Contains("NZDUSD") ||
                s.Contains("USDCHF") ||
                s.Contains("EURGBP") ||
                s.Contains("GBPJPY") ||
                s.Contains("AUDJPY") ||
                s.Contains("CADJPY");
        }
    }
}
