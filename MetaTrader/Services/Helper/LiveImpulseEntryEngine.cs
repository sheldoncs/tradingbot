using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class LiveImpulseEntryEngine
    {
        private class LiveImpulseProfile
        {
            public double BodyThreshold { get; set; }
            public double CounterTrendBoost { get; set; }

            public double EarlyBody { get; set; }
            public double EarlyDistance { get; set; }
            public double MinEmaDistance { get; set; }
            public double MaxEmaDistance { get; set; }
            public double EarlyBreakAllowance { get; set; }

            public double TransitionBody { get; set; }
            public double TransitionMaxEmaDistance { get; set; }
            public double TransitionBeforeBreakout { get; set; }
            public double TransitionBeyondBreakout { get; set; }

            public double NormalMaxBeyondBreakout { get; set; }

            // ============================================================
            // STRUCTURAL ESCAPE
            // ============================================================

            public double EscapeBody { get; set; }

            public double EscapeMinBreakAtr { get; set; }

            public double EscapeMaxBreakAtr { get; set; }

            public double EscapeMinEmaDistanceAtr { get; set; }

            public double EscapeMaxEmaDistanceAtr { get; set; }
        }


        // ================================================================
        // COUNTER-TREND REVERSAL CONFIRMATION STATE
        // ================================================================
        // A large first candle is only a REVERSAL CANDIDATE.
        // We do not enter immediately.  We wait for:
        //
        // 1) initial impulse + EMA9 reclaim/turn,
        // 2) approach/reclaim of EMA21,
        // 3) controlled pullback that holds around EMA9,
        // 4) renewed follow-through in the reversal direction.
        //
        // This is intentionally stateful because a genuine "higher low /
        // lower high + follow-through" cannot be confirmed from one tick.

        private class ReversalCandidate
        {
            public bool IsBuy { get; set; }

            public DateTime ArmedAtUtc { get; set; }

            public double AnchorPrice { get; set; }

            public double ExtremePrice { get; set; }

            public double PullbackPrice { get; set; }

            public bool PullbackSeen { get; set; }

            public bool TouchedEma21Zone { get; set; }
        }


        private static readonly object ReversalLock =
            new object();


        private static readonly System.Collections.Generic.Dictionary<string, ReversalCandidate>
            ReversalCandidates =
                new System.Collections.Generic.Dictionary<string, ReversalCandidate>();

        // Diagnostic sampling only. Does not affect trading decisions.
        // Periodic Gold HOLD snapshots capture the early phase below 0.45 ATR.
        private static readonly object GoldDiagnosticSync = new object();
        private static readonly System.Collections.Generic.Dictionary<string, DateTime>
            GoldDiagnosticLastSampleUtc = new System.Collections.Generic.Dictionary<string, DateTime>(
                System.StringComparer.OrdinalIgnoreCase);

        private static bool ShouldSampleGoldHold(string symbol)
        {
            DateTime now = DateTime.UtcNow;
            lock (GoldDiagnosticSync)
            {
                if (GoldDiagnosticLastSampleUtc.TryGetValue(symbol, out DateTime last) &&
                    (now - last).TotalSeconds < 30)
                    return false;

                GoldDiagnosticLastSampleUtc[symbol] = now;
                return true;
            }
        }

        private const bool EnableConfirmedReversals = false;
        public static bool TryEnter(
            string symbol,
            MqlRates liveCandle,
            double atr,
            double ema9,
            double ema9Previous,
            double ema21,
            double recentHigh,
            double recentLow,
            double bid,
            double ask,
            double momentum,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            try
            {
                // ========================================================
                // BASIC VALIDATION
                // ========================================================

                if (string.IsNullOrWhiteSpace(symbol))
                    return false;

                if (atr <= 0)
                    return false;

                if (bid <= 0 ||
                    ask <= 0 ||
                    ema9 <= 0 ||
                    ema21 <= 0)
                {
                    return false;
                }


                symbol =
                    symbol.ToUpperInvariant();


                LiveImpulseProfile profile =
                    GetProfile(symbol);


                // ========================================================
                // LIVE DIRECTIONAL BODY
                // ========================================================

                // Use the live midpoint for candle direction/body so spread
                // does not artificially inflate bullish or bearish body size.
                double liveMid =
                    (bid + ask) / 2.0;

                // Determine whether the live candle contains meaningful wick data.
                // The current FastEntryEngine constructs synthetic candles without
                // historical intrabar highs/lows.
                double goldCandleRange =
                    Math.Max(liveCandle.high - liveCandle.low, 0.0000001);

                double goldCandleBody =
                    Math.Abs(liveCandle.close - liveCandle.open);

                double goldTotalWick =
                    Math.Max(0.0, goldCandleRange - goldCandleBody);

                bool goldHasMeaningfulWicks =
                    goldTotalWick / goldCandleRange >= 0.05;

                double liveBullBody =
                    Math.Max(
                        0.0,
                        liveMid - liveCandle.open
                    );


                double liveBearBody =
                    Math.Max(
                        0.0,
                        liveCandle.open - liveMid
                    );


                double bodyAtrBuy =
                    liveBullBody /
                    atr;


                double bodyAtrSell =
                    liveBearBody /
                    atr;


                // ========================================================
                // DISTANCE FROM EMA9
                // ========================================================

                double distFromEmaBuy =
                    Math.Abs(
                        ask - ema9
                    ) / atr;


                double distFromEmaSell =
                    Math.Abs(
                        bid - ema9
                    ) / atr;


                // ========================================================
                // EMA DIRECTION / TREND ALIGNMENT
                // ========================================================

                bool ema9Rising =
                    ema9 >
                    ema9Previous;


                bool ema9Falling =
                    ema9 <
                    ema9Previous;


                bool overallBullish =
                    ema9 >
                    ema21;


                bool overallBearish =
                    ema9 <
                    ema21;


                // ========================================================
                // PRIMARY DIRECTION REGIME
                // ========================================================
                // EMA9 / EMA21 alignment is the MAIN entry condition.
                //
                // Once EMA9 is above EMA21, BUY entries are permitted and
                // SELL entries are blocked until EMA9 crosses back below.
                // Once EMA9 is below EMA21, SELL entries are permitted and
                // BUY entries are blocked until EMA9 crosses back above.
                //
                // This rule applies to ALL impulse paths, including
                // Structural Escape, Transition, Early and Normal impulse.

                bool buyRegimeAllowed =
                    overallBullish &&
                    ema9Rising;

                bool sellRegimeAllowed =
                    overallBearish &&
                    ema9Falling;


                // ========================================================
                // LIVE PRICE RELATIONSHIP TO EMA
                // ========================================================

                bool priceAboveEma9 =
                    ask >
                    ema9;


                bool priceBelowEma9 =
                    bid <
                    ema9;


                bool priceAboveEma21 =
                    ask >
                    ema21;


                bool priceBelowEma21 =
                    bid <
                    ema21;


                // ========================================================
                // BREAKOUT DISTANCES
                // ========================================================

                double breakoutDistanceBuy =
                    (ask - recentHigh) /
                    atr;


                double breakoutDistanceSell =
                    (recentLow - bid) /
                    atr;


                double distToBreakout =
                    (recentHigh - ask) /
                    atr;


                double distToBreakdown =
                    (bid - recentLow) /
                    atr;


                // ========================================================
                // LIVE CANDLE DIRECTION
                // ========================================================

                bool bullishLiveCandle =
                    liveMid >
                    liveCandle.open;


                bool bearishLiveCandle =
                    liveMid <
                    liveCandle.open;


                // ========================================================
                // LIVE WICK REJECTION PROTECTION
                // ========================================================
                // PURPOSE:
                //
                // A candle can still have a bearish/bullish body while the
                // opposite side is already rejecting the move.
                //
                // SELL:
                // - a large LOWER wick means price pushed down and bounced.
                // - do not enter a fresh SELL after that rejection is already
                //   visible.
                //
                // BUY:
                // - a large UPPER wick means price pushed up and fell back.
                // - do not enter a fresh BUY after that rejection is already
                //   visible.
                //
                // This guard is symmetrical and applies before every impulse
                // entry path below, including confirmed reversals, structural
                // escape, transition, early and normal impulse.
                // ========================================================

                double rejectionRange =
                    Math.Max(
                        liveCandle.high - liveCandle.low,
                        0.0000001
                    );


                double lowerWick =
                    Math.Max(
                        0.0,
                        Math.Min(
                            liveCandle.open,
                            liveMid
                        ) - liveCandle.low
                    );


                double upperWick =
                    Math.Max(
                        0.0,
                        liveCandle.high -
                        Math.Max(
                            liveCandle.open,
                            liveMid
                        )
                    );


                double lowerWickRatio =
                    lowerWick /
                    rejectionRange;


                double upperWickRatio =
                    upperWick /
                    rejectionRange;


                // --------------------------------------------------------
                // ASSET-SPECIFIC WICK THRESHOLD
                // --------------------------------------------------------
                // Gold and crypto naturally print larger intrabar wicks, so
                // they receive a little more tolerance than FX and indices.
                //
                // These are starting values and should be validated from logs.
                // --------------------------------------------------------

                double rejectionWickThreshold =
                    GetRejectionWickThreshold(
                        symbol
                    );


                bool sellMomentumRejecting =
                    bearishLiveCandle &&
                    lowerWickRatio >=
                        rejectionWickThreshold;


                bool buyMomentumRejecting =
                    bullishLiveCandle &&
                    upperWickRatio >=
                        rejectionWickThreshold;


                if (sellMomentumRejecting)
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ IMPULSE SELL BLOCKED - LOWER WICK REJECTION | " +
                        $"lowerWickRatio={lowerWickRatio:F2} | " +
                        $"threshold={rejectionWickThreshold:F2} | " +
                        $"bodyATR={bodyAtrSell:F2} | " +
                        $"liveMid={liveMid:F5} | " +
                        $"open={liveCandle.open:F5} | " +
                        $"low={liveCandle.low:F5} | " +
                        $"high={liveCandle.high:F5} | " +
                        $"reason=sellers pushed lower but price already rejected the low"
                    );


                    return false;
                }


                if (buyMomentumRejecting)
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ IMPULSE BUY BLOCKED - UPPER WICK REJECTION | " +
                        $"upperWickRatio={upperWickRatio:F2} | " +
                        $"threshold={rejectionWickThreshold:F2} | " +
                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"liveMid={liveMid:F5} | " +
                        $"open={liveCandle.open:F5} | " +
                        $"low={liveCandle.low:F5} | " +
                        $"high={liveCandle.high:F5} | " +
                        $"reason=buyers pushed higher but price already rejected the high"
                    );


                    return false;
                }


                // ========================================================
                // CONFIRMED COUNTER-TREND REVERSAL
                // ========================================================
                // IMPORTANT:
                //
                // A counter-trend candle no longer receives an immediate BUY
                // or SELL simply because it is large.  This path first arms
                // a candidate, then waits for a controlled pullback and a
                // fresh continuation push.
                //
                // Normal trend-aligned impulse entries below are unchanged.

                if (EnableConfirmedReversals && TryConfirmedCounterTrendReversal(
                    symbol,
                    liveCandle,
                    profile,
                    atr,
                    ema9,
                    ema21,
                    overallBullish,
                    overallBearish,
                    ema9Rising,
                    ema9Falling,
                    bullishLiveCandle,
                    bearishLiveCandle,
                    priceAboveEma9,
                    priceBelowEma9,
                    priceAboveEma21,
                    priceBelowEma21,
                    bodyAtrBuy,
                    bodyAtrSell,
                    distFromEmaBuy,
                    distFromEmaSell,
                    liveMid,
                    bid,
                    ask,
                    out TradeAction reversalAction))
                {
                    action =
                        reversalAction;

                    return true;
                }


                // ========================================================
                // STRONG LIVE REGIME OVERRIDE
                // ========================================================
                // PURPOSE:
                //
                // EMA9/EMA21 alignment can lag a violent live reversal.
                // Do NOT remove the normal EMA regime. Instead, allow a
                // very strong live move to temporarily override alignment
                // only when ALL of the following agree:
                //
                // BUY:
                // - live candle is strongly bullish
                // - body >= EscapeBody
                // - EMA9 is rising
                // - price is above EMA9 AND EMA21
                //
                // SELL:
                // - live candle is strongly bearish
                // - body >= EscapeBody
                // - EMA9 is falling
                // - price is below EMA9 AND EMA21
                //
                // This preserves the existing structure while preventing
                // a lagging EMA9/EMA21 cross from vetoing an obvious live
                // structural transition.
                // ========================================================

                bool strongBullishRegimeOverride =
                    bullishLiveCandle &&
                    bodyAtrBuy >= profile.EscapeBody &&
                    ema9Rising &&
                    priceAboveEma9 &&
                    priceAboveEma21;


                bool strongBearishRegimeOverride =
                    bearishLiveCandle &&
                    bodyAtrSell >= profile.EscapeBody &&
                    ema9Falling &&
                    priceBelowEma9 &&
                    priceBelowEma21;


                if (strongBullishRegimeOverride)
                {
                    buyRegimeAllowed = true;

                    LogFile.Write(
                        $"[{symbol}] 🔓 BULL REGIME OVERRIDE | " +
                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"escapeReq={profile.EscapeBody:F2} | " +
                        $"ema9Rising={ema9Rising} | " +
                        $"priceAboveEma9={priceAboveEma9} | " +
                        $"priceAboveEma21={priceAboveEma21} | " +
                        $"bullAligned={overallBullish} | " +
                        $"reason=strong live bullish transition"
                    );
                }


                if (strongBearishRegimeOverride)
                {
                    sellRegimeAllowed = true;

                    LogFile.Write(
                        $"[{symbol}] 🔓 BEAR REGIME OVERRIDE | " +
                        $"bodyATR={bodyAtrSell:F2} | " +
                        $"escapeReq={profile.EscapeBody:F2} | " +
                        $"ema9Falling={ema9Falling} | " +
                        $"priceBelowEma9={priceBelowEma9} | " +
                        $"priceBelowEma21={priceBelowEma21} | " +
                        $"bearAligned={overallBearish} | " +
                        $"reason=strong live bearish transition"
                    );
                }


                // ========================================================
                // GOLD ENTRY-LOCATION QUALITY GATE
                // ========================================================
                // PURPOSE:
                // Prevent XAUUSD / XAUEUR from entering at the extreme end of
                // an already-expanded live candle.  This runs BEFORE Gold
                // Acceleration so that acceleration cannot bypass anti-chase.
                //
                // This does NOT change trend direction and does NOT add
                // reversal entries.  It only rejects a late location when the
                // candle is already expanded AND price is near its extreme.
                // ========================================================

                if (IsGold(symbol))
                {
                    // FINAL XAU ENTRY-LOCATION GATE.
                    // This is deliberately evaluated before every Gold entry path
                    // (Acceleration, Structural Escape, Trend Resumption,
                    // Transition, Early and Normal impulse).
                    //
                    // A move is considered spent when price is already near the
                    // live-candle extreme AND has materially stretched from EMA9
                    // or beyond structure.  A second hard-extension condition
                    // catches exceptionally stretched moves even if the candle
                    // position has temporarily improved because of a wick.
                    // Gold-specific calibration: avoid vetoing the acceleration
                    // path solely because EMA distance is 1.10-1.35 ATR.
                    // Keep breakout-extension and EMA hard-distance guards.
                    const double goldLocationMinExpandedBodyAtr = 0.55;
                    const double goldLocationMaxCandlePosition = 0.84;
                    const double goldLocationMinEmaDistanceAtr = 0.90;
                    const double goldLocationMaxBeyondBreakAtr = 0.30;
                    const double goldLocationHardEmaDistanceAtr = 1.35;

                    double goldLocationRange =
                        Math.Max(
                            liveCandle.high - liveCandle.low,
                            0.0000001
                        );

                    // FastEntryEngine currently supplies a synthetic live H1 candle
                    // with high/low built from its open and the current midpoint.
                    // Such a candle has NO intrabar wick history. Bid/ask based
                    // positions on it can be > 1.0 and are not valid location tests.
                    double goldWickTolerance = Math.Max(atr * 0.0001, 0.0000001);
                    double goldLiveUpperWick = Math.Max(
                        0.0, liveCandle.high - Math.Max(liveCandle.open, liveMid));
                    double goldLiveLowerWick = Math.Max(
                        0.0, Math.Min(liveCandle.open, liveMid) - liveCandle.low);


                    double goldBuyPositionInCandle =
                        Math.Max(0.0, Math.Min(1.0,
                            (liveMid - liveCandle.low) / goldLocationRange));

                    double goldSellPositionInCandle =
                        Math.Max(0.0, Math.Min(1.0,
                            (liveCandle.high - liveMid) / goldLocationRange));

                    bool goldLateBuyLocation =
                        bullishLiveCandle &&
                        buyRegimeAllowed &&
                        bodyAtrBuy >= goldLocationMinExpandedBodyAtr &&
                        (
                            distFromEmaBuy >= goldLocationHardEmaDistanceAtr ||
                            // Retain a firm structural anti-chase threshold when
                            // the synthetic candle cannot provide a true location.
                            breakoutDistanceBuy >= profile.TransitionBeyondBreakout
                        );

                    bool goldLateSellLocation =
                        bearishLiveCandle &&
                        sellRegimeAllowed &&
                        bodyAtrSell >= goldLocationMinExpandedBodyAtr &&
                        (
                            distFromEmaSell >= goldLocationHardEmaDistanceAtr ||
                            breakoutDistanceSell >= profile.TransitionBeyondBreakout
                        );

                    if (goldLateBuyLocation)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ GOLD BUY BLOCKED - LATE ENTRY LOCATION | " +
                            $"bodyATR={bodyAtrBuy:F2} | " +
                            $"emaDist={distFromEmaBuy:F2} | " +
                            $"breakATR={breakoutDistanceBuy:F2} | " +
                            $"candlePosition={goldBuyPositionInCandle:F2} | " +
                            $"maxPosition={goldLocationMaxCandlePosition:F2} | " +
                            $"hardEmaDist={goldLocationHardEmaDistanceAtr:F2} | " +
                            $"meaningfulWicks={goldHasMeaningfulWicks} | " +
                            $"reason=expanded bullish candle is too late/extended; wait for next continuation opportunity"
                        );

                        return false;
                    }

                    if (goldLateSellLocation)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ GOLD SELL BLOCKED - LATE ENTRY LOCATION | " +
                            $"bodyATR={bodyAtrSell:F2} | " +
                            $"emaDist={distFromEmaSell:F2} | " +
                            $"breakATR={breakoutDistanceSell:F2} | " +
                            $"candlePosition={goldSellPositionInCandle:F2} | " +
                            $"maxPosition={goldLocationMaxCandlePosition:F2} | " +
                            $"hardEmaDist={goldLocationHardEmaDistanceAtr:F2} | " +
                            $"meaningfulWicks={goldHasMeaningfulWicks} | " +
                            $"reason=expanded bearish candle is too late/extended; wait for next continuation opportunity"
                        );

                        return false;
                    }
                }


                // ========================================================
                // GOLD ACCELERATION IMPULSE
                // ========================================================
                // PURPOSE:
                // Catch the "middle phase" of a Gold move where the existing
                // EMA9/EMA21 trend is already aligned and the live candle is
                // accelerating, but the move has not yet reached the normal
                // Gold transition / momentum threshold and has not yet become
                // late enough for the anti-chase guard to reject it.
                //
                // IMPORTANT:
                // - Gold only (XAUUSD / XAUEUR through IsGold()).
                // - No method signature changes.
                // - No caller changes.
                // - Existing wick-rejection guard has already run.
                // - Existing EMA regime must already agree with the direction.
                // - This does NOT bypass late-move protection: this path has
                //   its own tighter EMA-distance / breakout-extension limits.
                // - This does NOT lower the normal momentum thresholds used by
                //   the other impulse paths.
                // ========================================================

                if (IsGold(symbol))
                {
                    const double goldAccelerationBodyAtr = 0.35;
                    const double goldAccelerationMomentumMin = 0.30; // Gold early acceleration
                    const double goldAccelerationMinEmaDistanceAtr = 0.12;
                    const double goldAccelerationMaxEmaDistanceAtr = 1.35;
                    const double goldAccelerationMinEmaSpreadAtr = 0.10;
                    const double goldAccelerationMinEma9SlopeAtr = 0.025;
                    const double goldAccelerationMaxPreBreakAtr = 0.65;
                    const double goldAccelerationMaxBeyondBreakAtr = 0.35;
                    const double goldAccelerationMaxCandlePosition = 0.90;

                    double goldAccelerationEmaSpreadAtr =
                        Math.Abs(
                            ema9 - ema21
                        ) / atr;

                    double goldAccelerationEma9SlopeAtr =
                        Math.Abs(
                            ema9 - ema9Previous
                        ) / atr;

                    double goldAccelerationRange =
                        Math.Max(
                            liveCandle.high - liveCandle.low,
                            0.0000001
                        );

                    double goldAccelerationBuyPosition =
                        Math.Max(0.0, Math.Min(1.0,
                            (liveMid - liveCandle.low) / goldAccelerationRange));

                    double goldAccelerationSellPosition =
                        Math.Max(0.0, Math.Min(1.0,
                            (liveCandle.high - liveMid) / goldAccelerationRange));

                    bool goldAccelerationBuy =
                        bullishLiveCandle &&
                        overallBullish &&
                        buyRegimeAllowed &&
                        ema9Rising &&
                        priceAboveEma9 &&
                        priceAboveEma21 &&
                        bodyAtrBuy >=
                            goldAccelerationBodyAtr &&
                        momentum >=
                            goldAccelerationMomentumMin &&
                        distFromEmaBuy >=
                            goldAccelerationMinEmaDistanceAtr &&
                        distFromEmaBuy <=
                            goldAccelerationMaxEmaDistanceAtr &&
                        goldAccelerationEmaSpreadAtr >=
                            goldAccelerationMinEmaSpreadAtr &&
                        goldAccelerationEma9SlopeAtr >=
                            goldAccelerationMinEma9SlopeAtr &&
                        breakoutDistanceBuy >=
                            -goldAccelerationMaxPreBreakAtr &&
                        breakoutDistanceBuy <=
                            goldAccelerationMaxBeyondBreakAtr;

                    if (goldAccelerationBuy)
                    {
                        action =
                            TradeAction.EnterBuy;

                        LogFile.Write(
                            $"[{symbol}] 🚀 GOLD ACCELERATION BUY | " +
                            $"bodyATR={bodyAtrBuy:F2} | " +
                            $"bodyReq={goldAccelerationBodyAtr:F2} | " +
                            $"momentum={momentum:F2} | " +
                            $"momentumReq={goldAccelerationMomentumMin:F2} | " +
                            $"emaDist={distFromEmaBuy:F2} | " +
                            $"emaDistRange={goldAccelerationMinEmaDistanceAtr:F2}-" +
                            $"{goldAccelerationMaxEmaDistanceAtr:F2} | " +
                            $"emaSpreadATR={goldAccelerationEmaSpreadAtr:F2} | " +
                            $"ema9SlopeATR={goldAccelerationEma9SlopeAtr:F2} | " +
                            $"breakATR={breakoutDistanceBuy:F2} | " +
                            $"candlePosition={goldAccelerationBuyPosition:F2} | " +
                            $"NOTE=Gold bullish acceleration entered before late-chase stage"
                        );

                        return true;
                    }

                    bool goldAccelerationSell =
                        bearishLiveCandle &&
                        overallBearish &&
                        sellRegimeAllowed &&
                        ema9Falling &&
                        priceBelowEma9 &&
                        priceBelowEma21 &&
                        bodyAtrSell >=
                            goldAccelerationBodyAtr &&
                        momentum >=
                            goldAccelerationMomentumMin &&
                        distFromEmaSell >=
                            goldAccelerationMinEmaDistanceAtr &&
                        distFromEmaSell <=
                            goldAccelerationMaxEmaDistanceAtr &&
                        goldAccelerationEmaSpreadAtr >=
                            goldAccelerationMinEmaSpreadAtr &&
                        goldAccelerationEma9SlopeAtr >=
                            goldAccelerationMinEma9SlopeAtr &&
                        breakoutDistanceSell >=
                            -goldAccelerationMaxPreBreakAtr &&
                        breakoutDistanceSell <=
                            goldAccelerationMaxBeyondBreakAtr;

                    if (goldAccelerationSell)
                    {
                        action =
                            TradeAction.EnterSell;

                        LogFile.Write(
                            $"[{symbol}] 🚀 GOLD ACCELERATION SELL | " +
                            $"bodyATR={bodyAtrSell:F2} | " +
                            $"bodyReq={goldAccelerationBodyAtr:F2} | " +
                            $"momentum={momentum:F2} | " +
                            $"momentumReq={goldAccelerationMomentumMin:F2} | " +
                            $"emaDist={distFromEmaSell:F2} | " +
                            $"emaDistRange={goldAccelerationMinEmaDistanceAtr:F2}-" +
                            $"{goldAccelerationMaxEmaDistanceAtr:F2} | " +
                            $"emaSpreadATR={goldAccelerationEmaSpreadAtr:F2} | " +
                            $"ema9SlopeATR={goldAccelerationEma9SlopeAtr:F2} | " +
                            $"breakATR={breakoutDistanceSell:F2} | " +
                            $"candlePosition={goldAccelerationSellPosition:F2} | " +
                            $"NOTE=Gold bearish acceleration entered before late-chase stage"
                        );

                        return true;
                    }
                }


                // ========================================================
                // ANTI-CHASE / EXTENDED IMPULSE PROTECTION
                // ========================================================
                // Strong momentum is still required by the existing impulse
                // paths below. This guard only prevents an entry when that
                // same strong candle has already stretched too far away from
                // EMA9 and/or too far beyond the breakout level.
                //
                // IMPORTANT:
                // - No method signatures are changed.
                // - No existing impulse path is removed.
                // - No existing profile values are changed.
                // - This applies symmetrically to BUY and SELL impulses.
                //
                // The threshold is derived from the existing profile so each
                // asset class keeps its current personality.

                double chaseBodyThreshold =
                    Math.Max(
                        profile.BodyThreshold,
                        profile.TransitionBody
                    ) * 1.35;


                double chaseEmaDistance =
                    Math.Min(
                        profile.MaxEmaDistance,
                        profile.TransitionMaxEmaDistance
                    );


                double chaseBreakoutDistance =
                    profile.NormalMaxBeyondBreakout;


                // ========================================================
                // EXTREME DISTANCE PROTECTION
                // ========================================================
                // A trade can still be too late even if bodyATR has not yet
                // reached chaseBodyThreshold.
                //
                // If price is already materially beyond the normal chase
                // distance from EMA9 or the breakout level, block the entry
                // and wait for a pullback / retest.

                double extremeEmaDistance =
                    IsGold(symbol)
                        ? Math.Max(chaseEmaDistance * 1.15, 1.35)
                        : chaseEmaDistance * 1.15;


                double extremeBreakoutDistance =
                    chaseBreakoutDistance * 1.15;


                // ========================================================
                // LIVE CANDLE ENTRY LOCATION
                // ========================================================
                // Avoid BUYING near the top of an already expanded bullish
                // candle and avoid SELLING near the bottom of an already
                // expanded bearish candle.

                double liveRange =
                    Math.Max(
                        liveCandle.high - liveCandle.low,
                        0.0000001
                    );


                double buyPositionInCandle =
                    (ask - liveCandle.low) /
                    liveRange;


                double sellPositionInCandle =
                    (liveCandle.high - bid) /
                    liveRange;


                bool extendedImpulseBuy =
                    bullishLiveCandle &&
                    buyRegimeAllowed &&
                    (
                        (
                            bodyAtrBuy >= chaseBodyThreshold &&
                            (
                                distFromEmaBuy >= chaseEmaDistance ||
                                breakoutDistanceBuy >= chaseBreakoutDistance
                            )
                        )
                        ||
                        distFromEmaBuy >= extremeEmaDistance
                        ||
                        breakoutDistanceBuy >= extremeBreakoutDistance
                        ||
                        (
                            bodyAtrBuy >= profile.BodyThreshold &&
                            buyPositionInCandle >= 0.85 &&
                            distFromEmaBuy >=
                                profile.TransitionMaxEmaDistance * 0.80
                        )
                    );


                bool extendedImpulseSell =
                    bearishLiveCandle &&
                    sellRegimeAllowed &&
                    (
                        (
                            bodyAtrSell >= chaseBodyThreshold &&
                            (
                                distFromEmaSell >= chaseEmaDistance ||
                                breakoutDistanceSell >= chaseBreakoutDistance
                            )
                        )
                        ||
                        distFromEmaSell >= extremeEmaDistance
                        ||
                        breakoutDistanceSell >= extremeBreakoutDistance
                        ||
                        (
                            bodyAtrSell >= profile.BodyThreshold &&
                            sellPositionInCandle >= 0.85 &&
                            distFromEmaSell >=
                                profile.TransitionMaxEmaDistance * 0.80
                        )
                    );


                if (extendedImpulseBuy)
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ IMPULSE BUY BLOCKED - EXTENDED / DO NOT CHASE | " +
                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"chaseBodyReq={chaseBodyThreshold:F2} | " +
                        $"emaDist={distFromEmaBuy:F2} | " +
                        $"maxChaseEmaDist={chaseEmaDistance:F2} | " +
                        $"extremeEmaDist={extremeEmaDistance:F2} | " +
                        $"breakATR={breakoutDistanceBuy:F2} | " +
                        $"maxChaseBreak={chaseBreakoutDistance:F2} | " +
                        $"extremeBreak={extremeBreakoutDistance:F2} | " +
                        $"candlePosition={buyPositionInCandle:F2} | " +
                        $"NOTE=Momentum is valid but BUY entry is too extended; wait for pullback/retest"
                    );


                    return false;
                }


                if (extendedImpulseSell)
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ IMPULSE SELL BLOCKED - EXTENDED / DO NOT CHASE | " +
                        $"bodyATR={bodyAtrSell:F2} | " +
                        $"chaseBodyReq={chaseBodyThreshold:F2} | " +
                        $"emaDist={distFromEmaSell:F2} | " +
                        $"maxChaseEmaDist={chaseEmaDistance:F2} | " +
                        $"extremeEmaDist={extremeEmaDistance:F2} | " +
                        $"breakATR={breakoutDistanceSell:F2} | " +
                        $"maxChaseBreak={chaseBreakoutDistance:F2} | " +
                        $"extremeBreak={extremeBreakoutDistance:F2} | " +
                        $"candlePosition={sellPositionInCandle:F2} | " +
                        $"NOTE=Momentum is valid but SELL entry is too extended; wait for pullback/retest"
                    );


                    return false;
                }


                // ================================================================
                // GOLD EARLY BULLISH TRANSITION
                // ================================================================
                // Recognize a strong EMA21 reclaim while EMA9 is still below EMA21.
                //
                // This pathway does NOT:
                // - enable the disabled counter-trend reversal system;
                // - bypass the existing wick or anti-chase checks;
                // - change FX, CRYPTO, or CASH;
                // - bypass downstream FastEntryEngine SR/M15 approval.
                // ================================================================

                if (IsGold(symbol) &&
                    overallBearish &&
                    bullishLiveCandle &&
                    ema9Rising &&
                    priceAboveEma9 &&
                    priceAboveEma21)
                {
                    const double minTransitionBodyAtr = 0.65;
                    const double minTransitionMomentum = 0.50;
                    const double maxTransitionEmaDistanceAtr = 0.95;
                    const double maxTransitionEmaSpreadAtr = 0.65;
                    const double maxPreBreakDistanceAtr = 1.10;
                    const double maxBeyondBreakDistanceAtr = 0.20;

                    double transitionSpreadAtr =
                        Math.Abs(ema9 - ema21) / atr;

                    bool strongBullishRecovery =
                        bodyAtrBuy >= minTransitionBodyAtr &&
                        momentum >= minTransitionMomentum;

                    bool acceptableLocation =
                        distFromEmaBuy >= profile.MinEmaDistance &&
                        distFromEmaBuy <= maxTransitionEmaDistanceAtr &&
                        transitionSpreadAtr <= maxTransitionEmaSpreadAtr &&
                        breakoutDistanceBuy >= -maxPreBreakDistanceAtr &&
                        breakoutDistanceBuy <= maxBeyondBreakDistanceAtr;

                    if (strongBullishRecovery && acceptableLocation)
                    {
                        action = TradeAction.EnterBuy;

                        LogFile.Write(
                            $"[{symbol}] GOLD EARLY BULLISH TRANSITION BUY | " +
                            $"bodyATR={bodyAtrBuy:F2} | " +
                            $"momentum={momentum:F2} | " +
                            $"emaDistATR={distFromEmaBuy:F2} | " +
                            $"emaSpreadATR={transitionSpreadAtr:F2} | " +
                            $"breakATR={breakoutDistanceBuy:F2} | " +
                            $"ema9={ema9:F5} | " +
                            $"ema21={ema21:F5} | " +
                            $"reason=Strong bullish recovery through EMA21 before EMA crossover"
                        );

                        return true;
                    }
                }

                // ========================================================
                // NORMAL IMPULSE BODY REQUIREMENT
                // ========================================================

                double buyRequiredBody =
                    overallBullish

                        ? profile.BodyThreshold

                        : profile.BodyThreshold +
                          profile.CounterTrendBoost;


                double sellRequiredBody =
                    overallBearish

                        ? profile.BodyThreshold

                        : profile.BodyThreshold +
                          profile.CounterTrendBoost;


                // ========================================================
                // GOLD STRUCTURAL ESCAPE MOMENTUM PROTECTION
                // ========================================================
                // Structural Escape previously validated geometry/body/EMA
                // structure but did not require momentum. For Gold, require
                // real follow-through before promoting a fresh break.
                // Other symbols keep their existing Structural Escape behavior.
                const double goldStructuralEscapeMomentumMin = 0.55;

                // ========================================================
                // STRUCTURAL ESCAPE BUY
                //
                // Strong structural move, but direction must still agree
                // with the primary EMA9 / EMA21 regime.
                // ========================================================

                bool escapeBuy =
                    bullishLiveCandle &&

                    buyRegimeAllowed &&

                    (!IsGold(symbol) ||
                     momentum >= goldStructuralEscapeMomentumMin) &&

                    bodyAtrBuy >=
                        profile.EscapeBody &&

                    priceAboveEma9 &&

                    breakoutDistanceBuy >=
                        profile.EscapeMinBreakAtr &&

                    breakoutDistanceBuy <=
                        profile.EscapeMaxBreakAtr &&

                    distFromEmaBuy >=
                        profile.EscapeMinEmaDistanceAtr &&

                    distFromEmaBuy <=
                        profile.EscapeMaxEmaDistanceAtr;


                if (escapeBuy)
                {
                    action =
                        TradeAction.EnterBuy;


                    LogFile.Write(
                        $"[{symbol}] 🔥 STRUCTURAL ESCAPE BUY | " +

                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"required={profile.EscapeBody:F2} | " +

                        $"momentum={momentum:F2} | " +
                        $"goldMomentumReq={(IsGold(symbol) ? goldStructuralEscapeMomentumMin : 0.0):F2} | " +

                        $"breakATR={breakoutDistanceBuy:F2} | " +

                        $"breakRange=" +
                        $"{profile.EscapeMinBreakAtr:F2}-" +
                        $"{profile.EscapeMaxBreakAtr:F2} | " +

                        $"emaDist={distFromEmaBuy:F2} | " +

                        $"ema9={ema9:F5} | " +
                        $"ema9Prev={ema9Previous:F5} | " +
                        $"ema21={ema21:F5} | " +

                        $"ema9Rising={ema9Rising} | " +
                        $"ema9Falling={ema9Falling} | " +

                        $"bullAligned={overallBullish} | " +
                        $"bearAligned={overallBearish} | " +

                        $"NOTE=Structural escape confirmed inside " +
                        $"bullish EMA9/EMA21 regime"
                    );


                    return true;
                }


                // ========================================================
                // STRUCTURAL ESCAPE SELL
                //
                // Strong structural breakdown, but direction must still
                // agree with the primary EMA9 / EMA21 regime.
                // ========================================================

                bool escapeSell =
                    bearishLiveCandle &&

                    sellRegimeAllowed &&

                    (!IsGold(symbol) ||
                     momentum >= goldStructuralEscapeMomentumMin) &&

                    bodyAtrSell >=
                        profile.EscapeBody &&

                    priceBelowEma9 &&

                    breakoutDistanceSell >=
                        profile.EscapeMinBreakAtr &&

                    breakoutDistanceSell <=
                        profile.EscapeMaxBreakAtr &&

                    distFromEmaSell >=
                        profile.EscapeMinEmaDistanceAtr &&

                    distFromEmaSell <=
                        profile.EscapeMaxEmaDistanceAtr;


                if (escapeSell)
                {
                    action =
                        TradeAction.EnterSell;


                    LogFile.Write(
                        $"[{symbol}] 🔥 STRUCTURAL ESCAPE SELL | " +

                        $"bodyATR={bodyAtrSell:F2} | " +
                        $"required={profile.EscapeBody:F2} | " +

                        $"momentum={momentum:F2} | " +
                        $"goldMomentumReq={(IsGold(symbol) ? goldStructuralEscapeMomentumMin : 0.0):F2} | " +

                        $"breakATR={breakoutDistanceSell:F2} | " +

                        $"breakRange=" +
                        $"{profile.EscapeMinBreakAtr:F2}-" +
                        $"{profile.EscapeMaxBreakAtr:F2} | " +

                        $"emaDist={distFromEmaSell:F2} | " +

                        $"ema9={ema9:F5} | " +
                        $"ema9Prev={ema9Previous:F5} | " +
                        $"ema21={ema21:F5} | " +

                        $"ema9Rising={ema9Rising} | " +
                        $"ema9Falling={ema9Falling} | " +

                        $"bullAligned={overallBullish} | " +
                        $"bearAligned={overallBearish} | " +

                        $"NOTE=Structural escape confirmed inside " +
                        $"bearish EMA9/EMA21 regime"
                    );


                    return true;
                }


                // ========================================================
                // GOLD TREND RESUMPTION IMPULSE
                // ========================================================
                // PURPOSE:
                // Catch a fresh continuation push after Gold has pulled back /
                // retested the EMA9 area inside an already established EMA9/EMA21
                // trend.
                //
                // This is intentionally different from Transition / Early / Normal
                // impulse because it does NOT require price to already be close to
                // the previous high / low.
                //
                // SAFETY:
                // - Gold only.
                // - Existing wick-rejection protection has already run.
                // - Existing anti-chase protection has already run.
                // - EMA9/EMA21 regime must already agree with the trade direction.
                // - The current H1 candle must have STARTED near the prior EMA9,
                //   which acts as evidence of a pullback / retest before resumption.
                // ========================================================

                if (IsGold(symbol))
                {
                    const double goldResumeBodyAtr = 0.50;
                    const double goldResumeMomentumMin = 0.60;
                    const double goldResumeRetestZoneAtr = 0.35;
                    const double goldResumeRetestSideToleranceAtr = 0.10;
                    const double goldResumeMinEmaDistanceAtr = 0.08;
                    const double goldResumeMaxEmaDistanceAtr = 0.80;


                    // ----------------------------------------------------
                    // Was the current candle opened around the PREVIOUS
                    // EMA9?  Using ema9Previous avoids judging the candle
                    // start against an EMA9 that is itself moving live.
                    // ----------------------------------------------------

                    double goldResumeOpenToEma9Atr =
                        Math.Abs(
                            liveCandle.open - ema9Previous
                        ) / atr;


                    bool goldBuyRetestSeen =
                        goldResumeOpenToEma9Atr <=
                            goldResumeRetestZoneAtr &&

                        liveCandle.open <=
                            ema9Previous +
                            (goldResumeRetestSideToleranceAtr * atr);


                    bool goldSellRetestSeen =
                        goldResumeOpenToEma9Atr <=
                            goldResumeRetestZoneAtr &&

                        liveCandle.open >=
                            ema9Previous -
                            (goldResumeRetestSideToleranceAtr * atr);


                    // ====================================================
                    // GOLD TREND RESUMPTION BUY
                    // ====================================================

                    bool goldTrendResumeBuyGeometry =
                        bullishLiveCandle &&
                        overallBullish &&
                        buyRegimeAllowed &&
                        ema9Rising &&
                        goldBuyRetestSeen &&
                        priceAboveEma9 &&
                        priceAboveEma21 &&
                        bodyAtrBuy >= goldResumeBodyAtr &&
                        distFromEmaBuy >= goldResumeMinEmaDistanceAtr &&
                        distFromEmaBuy <= goldResumeMaxEmaDistanceAtr;

                    if (goldTrendResumeBuyGeometry &&
                        momentum < goldResumeMomentumMin)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ GOLD TREND RESUMPTION BUY BLOCKED - WEAK MOMENTUM | " +
                            $"momentum={momentum:F2} | " +
                            $"required={goldResumeMomentumMin:F2} | " +
                            $"bodyATR={bodyAtrBuy:F2} | " +
                            $"emaDist={distFromEmaBuy:F2} | " +
                            $"openToPrevEma9ATR={goldResumeOpenToEma9Atr:F2}"
                        );
                    }

                    bool goldTrendResumeBuy =
                        bullishLiveCandle &&

                        overallBullish &&
                        buyRegimeAllowed &&

                        ema9Rising &&

                        goldBuyRetestSeen &&

                        priceAboveEma9 &&
                        priceAboveEma21 &&

                        momentum >=
                            goldResumeMomentumMin &&

                        bodyAtrBuy >=
                            goldResumeBodyAtr &&

                        distFromEmaBuy >=
                            goldResumeMinEmaDistanceAtr &&

                        distFromEmaBuy <=
                            goldResumeMaxEmaDistanceAtr;


                    if (goldTrendResumeBuy)
                    {
                        action =
                            TradeAction.EnterBuy;


                        LogFile.Write(
                            $"[{symbol}] 🔄 GOLD TREND RESUMPTION BUY | " +

                            $"bodyATR={bodyAtrBuy:F2} | " +

                            $"required={goldResumeBodyAtr:F2} | " +

                            $"momentum={momentum:F2} | " +
                            $"momentumReq={goldResumeMomentumMin:F2} | " +

                            $"emaDist={distFromEmaBuy:F2} | " +

                            $"emaRange=" +
                            $"{goldResumeMinEmaDistanceAtr:F2}-" +
                            $"{goldResumeMaxEmaDistanceAtr:F2} | " +

                            $"openToPrevEma9ATR={goldResumeOpenToEma9Atr:F2} | " +

                            $"retestZone={goldResumeRetestZoneAtr:F2} | " +

                            $"ema9={ema9:F5} | " +

                            $"ema9Prev={ema9Previous:F5} | " +

                            $"ema21={ema21:F5} | " +

                            $"ema9Rising={ema9Rising} | " +

                            $"trendAligned={overallBullish} | " +

                            $"NOTE=Gold bullish trend resumed after EMA9-area retest"
                        );


                        return true;
                    }


                    // ====================================================
                    // GOLD TREND RESUMPTION SELL
                    // ====================================================

                    bool goldTrendResumeSellGeometry =
                        bearishLiveCandle &&
                        overallBearish &&
                        sellRegimeAllowed &&
                        ema9Falling &&
                        goldSellRetestSeen &&
                        priceBelowEma9 &&
                        priceBelowEma21 &&
                        bodyAtrSell >= goldResumeBodyAtr &&
                        distFromEmaSell >= goldResumeMinEmaDistanceAtr &&
                        distFromEmaSell <= goldResumeMaxEmaDistanceAtr;

                    if (goldTrendResumeSellGeometry &&
                        momentum < goldResumeMomentumMin)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ GOLD TREND RESUMPTION SELL BLOCKED - WEAK MOMENTUM | " +
                            $"momentum={momentum:F2} | " +
                            $"required={goldResumeMomentumMin:F2} | " +
                            $"bodyATR={bodyAtrSell:F2} | " +
                            $"emaDist={distFromEmaSell:F2} | " +
                            $"openToPrevEma9ATR={goldResumeOpenToEma9Atr:F2}"
                        );
                    }

                    bool goldTrendResumeSell =
                        bearishLiveCandle &&

                        overallBearish &&
                        sellRegimeAllowed &&

                        ema9Falling &&

                        goldSellRetestSeen &&

                        priceBelowEma9 &&
                        priceBelowEma21 &&

                        momentum >=
                            goldResumeMomentumMin &&

                        bodyAtrSell >=
                            goldResumeBodyAtr &&

                        distFromEmaSell >=
                            goldResumeMinEmaDistanceAtr &&

                        distFromEmaSell <=
                            goldResumeMaxEmaDistanceAtr;


                    if (goldTrendResumeSell)
                    {
                        action =
                            TradeAction.EnterSell;


                        LogFile.Write(
                            $"[{symbol}] 🔄 GOLD TREND RESUMPTION SELL | " +

                            $"bodyATR={bodyAtrSell:F2} | " +

                            $"required={goldResumeBodyAtr:F2} | " +

                            $"momentum={momentum:F2} | " +
                            $"momentumReq={goldResumeMomentumMin:F2} | " +

                            $"emaDist={distFromEmaSell:F2} | " +

                            $"emaRange=" +
                            $"{goldResumeMinEmaDistanceAtr:F2}-" +
                            $"{goldResumeMaxEmaDistanceAtr:F2} | " +

                            $"openToPrevEma9ATR={goldResumeOpenToEma9Atr:F2} | " +

                            $"retestZone={goldResumeRetestZoneAtr:F2} | " +

                            $"ema9={ema9:F5} | " +

                            $"ema9Prev={ema9Previous:F5} | " +

                            $"ema21={ema21:F5} | " +

                            $"ema9Falling={ema9Falling} | " +

                            $"trendAligned={overallBearish} | " +

                            $"NOTE=Gold bearish trend resumed after EMA9-area retest"
                        );


                        return true;
                    }
                }


                // ========================================================
                // TRANSITION IMPULSE BUY
                // ========================================================

                const double goldTransitionMomentumMin = 0.50;

                // Gold was trading well with this older baseline.
                // Keep the original transition logic, but allow an already
                // established GOLD trend to resume from farther below the
                // previous high instead of waiting until price is within
                // only the original 0.35 ATR window.
                //
                // IMPORTANT: this does NOT loosen counter-trend entries.
                // If EMA9 is not already above EMA21, the original profile
                // value is used.
                double transitionBeforeBreakoutBuy =
                    overallBullish && IsGold(symbol)
                        ? 1.10
                        : profile.TransitionBeforeBreakout;


                bool nearOrBreakingHigh =
                    breakoutDistanceBuy >=
                        -transitionBeforeBreakoutBuy &&

                    breakoutDistanceBuy <=
                        profile.TransitionBeyondBreakout;


                bool transitionImpulseBuy =
                    bullishLiveCandle &&

                    buyRegimeAllowed &&

                    (!IsGold(symbol) ||
                     momentum >= goldTransitionMomentumMin) &&

                    bodyAtrBuy >=
                        profile.TransitionBody &&

                    ema9Rising &&

                    priceAboveEma9 &&

                    distFromEmaBuy >=
                        profile.MinEmaDistance &&

                    distFromEmaBuy <=
                        profile.TransitionMaxEmaDistance &&

                    nearOrBreakingHigh;


                if (transitionImpulseBuy)
                {
                    action =
                        TradeAction.EnterBuy;


                    LogFile.Write(
                        $"[{symbol}] 🚀 TRANSITION IMPULSE BUY | " +

                        $"bodyATR={bodyAtrBuy:F2} | " +

                        $"required=" +
                        $"{profile.TransitionBody:F2} | " +

                        $"momentum={momentum:F2} | " +
                        $"goldMomentumReq={(IsGold(symbol) ? goldTransitionMomentumMin : 0.0):F2} | " +

                        $"emaDist={distFromEmaBuy:F2} | " +

                        $"breakDist=" +
                        $"{breakoutDistanceBuy:F2} | " +

                        $"preBreakAllowance={transitionBeforeBreakoutBuy:F2} | " +

                        $"ema9Rising={ema9Rising} | " +

                        $"trendAligned={overallBullish} | " +

                        $"ema9={ema9:F5} | " +

                        $"ema21={ema21:F5}"
                    );


                    return true;
                }


                // ========================================================
                // TRANSITION IMPULSE SELL
                // ========================================================

                // Mirror the same change for an already established
                // bearish GOLD trend. Counter-trend sells still use the
                // original profile allowance.
                double transitionBeforeBreakoutSell =
                    overallBearish && IsGold(symbol)
                        ? 1.10
                        : profile.TransitionBeforeBreakout;


                bool nearOrBreakingLow =
                    breakoutDistanceSell >=
                        -transitionBeforeBreakoutSell &&

                    breakoutDistanceSell <=
                        profile.TransitionBeyondBreakout;


                bool transitionImpulseSell =
                    bearishLiveCandle &&

                    sellRegimeAllowed &&

                    (!IsGold(symbol) ||
                     momentum >= goldTransitionMomentumMin) &&

                    bodyAtrSell >=
                        profile.TransitionBody &&

                    ema9Falling &&

                    priceBelowEma9 &&

                    distFromEmaSell >=
                        profile.MinEmaDistance &&

                    distFromEmaSell <=
                        profile.TransitionMaxEmaDistance &&

                    nearOrBreakingLow;


                if (transitionImpulseSell)
                {
                    action =
                        TradeAction.EnterSell;


                    LogFile.Write(
                        $"[{symbol}] 🚀 TRANSITION IMPULSE SELL | " +

                        $"bodyATR={bodyAtrSell:F2} | " +

                        $"required=" +
                        $"{profile.TransitionBody:F2} | " +

                        $"momentum={momentum:F2} | " +
                        $"goldMomentumReq={(IsGold(symbol) ? goldTransitionMomentumMin : 0.0):F2} | " +

                        $"emaDist={distFromEmaSell:F2} | " +

                        $"breakDist=" +
                        $"{breakoutDistanceSell:F2} | " +

                        $"preBreakAllowance={transitionBeforeBreakoutSell:F2} | " +

                        $"ema9Falling={ema9Falling} | " +

                        $"trendAligned={overallBearish} | " +

                        $"ema9={ema9:F5} | " +

                        $"ema21={ema21:F5}"
                    );


                    return true;
                }


                // ========================================================
                // EARLY IMPULSE BUY
                // ========================================================

                bool earlyImpulseBuy =
                    bullishLiveCandle &&

                    buyRegimeAllowed &&

                    bodyAtrBuy >=
                        profile.EarlyBody &&

                    priceAboveEma9 &&

                    ema9Rising &&

                    distFromEmaBuy >
                        profile.MinEmaDistance &&

                    distFromEmaBuy <
                        profile.MaxEmaDistance &&

                    distToBreakout >
                        -profile.EarlyBreakAllowance &&

                    distToBreakout <
                        profile.EarlyDistance;


                if (earlyImpulseBuy)
                {
                    action =
                        TradeAction.EnterBuy;


                    LogFile.Write(
                        $"[{symbol}] ⚡ EARLY IMPULSE BUY | " +

                        $"distToBreakout=" +
                        $"{distToBreakout:F2} | " +

                        $"breakAllowance=" +
                        $"{profile.EarlyBreakAllowance:F2} | " +

                        $"bodyATR={bodyAtrBuy:F2} | " +

                        $"required=" +
                        $"{profile.EarlyBody:F2} | " +

                        $"emaDist={distFromEmaBuy:F2} | " +

                        $"ema9Rising={ema9Rising}"
                    );


                    return true;
                }


                // ========================================================
                // EARLY IMPULSE SELL
                // ========================================================

                bool earlyImpulseSell =
                    bearishLiveCandle &&

                    sellRegimeAllowed &&

                    bodyAtrSell >=
                        profile.EarlyBody &&

                    priceBelowEma9 &&

                    ema9Falling &&

                    distFromEmaSell >
                        profile.MinEmaDistance &&

                    distFromEmaSell <
                        profile.MaxEmaDistance &&

                    distToBreakdown >
                        -profile.EarlyBreakAllowance &&

                    distToBreakdown <
                        profile.EarlyDistance;


                if (earlyImpulseSell)
                {
                    action =
                        TradeAction.EnterSell;


                    LogFile.Write(
                        $"[{symbol}] ⚡ EARLY IMPULSE SELL | " +

                        $"distToBreakdown=" +
                        $"{distToBreakdown:F2} | " +

                        $"breakAllowance=" +
                        $"{profile.EarlyBreakAllowance:F2} | " +

                        $"bodyATR={bodyAtrSell:F2} | " +

                        $"required=" +
                        $"{profile.EarlyBody:F2} | " +

                        $"emaDist={distFromEmaSell:F2} | " +

                        $"ema9Falling={ema9Falling}"
                    );


                    return true;
                }


                // ========================================================
                // NORMAL IMPULSE BUY
                // ========================================================

                bool impulseBuy =
                    bullishLiveCandle &&

                    buyRegimeAllowed &&

                    bodyAtrBuy >=
                        buyRequiredBody &&

                    ask >
                        recentHigh &&

                    priceAboveEma9 &&

                    distFromEmaBuy <
                        profile.MaxEmaDistance &&

                    breakoutDistanceBuy <=
                        profile.NormalMaxBeyondBreakout;


                if (impulseBuy)
                {
                    action =
                        TradeAction.EnterBuy;


                    LogFile.Write(
                        $"[{symbol}] ⚡ IMPULSE BUY | " +

                        $"bodyATR={bodyAtrBuy:F2} | " +

                        $"required={buyRequiredBody:F2} | " +

                        $"beyondBreakout=" +
                        $"{breakoutDistanceBuy:F2} | " +

                        $"maxBeyond=" +
                        $"{profile.NormalMaxBeyondBreakout:F2} | " +

                        $"distEMA={distFromEmaBuy:F2} | " +

                        $"trendAligned={overallBullish} | " +

                        $"ema9Rising={ema9Rising}"
                    );


                    return true;
                }


                // ========================================================
                // NORMAL IMPULSE SELL
                // ========================================================

                bool impulseSell =
                    bearishLiveCandle &&

                    sellRegimeAllowed &&

                    bodyAtrSell >=
                        sellRequiredBody &&

                    bid <
                        recentLow &&

                    priceBelowEma9 &&

                    distFromEmaSell <
                        profile.MaxEmaDistance &&

                    breakoutDistanceSell <=
                        profile.NormalMaxBeyondBreakout;


                if (impulseSell)
                {
                    action =
                        TradeAction.EnterSell;


                    LogFile.Write(
                        $"[{symbol}] ⚡ IMPULSE SELL | " +

                        $"bodyATR={bodyAtrSell:F2} | " +

                        $"required={sellRequiredBody:F2} | " +

                        $"beyondBreakout=" +
                        $"{breakoutDistanceSell:F2} | " +

                        $"maxBeyond=" +
                        $"{profile.NormalMaxBeyondBreakout:F2} | " +

                        $"distEMA={distFromEmaSell:F2} | " +

                        $"trendAligned={overallBearish} | " +

                        $"ema9Falling={ema9Falling}"
                    );


                    return true;
                }


                // ========================================================
                // DIAGNOSTICS
                // ========================================================

                // GOLD DIAGNOSTIC-ONLY: runs only after every entry path
                // has declined. No thresholds or trade decisions are changed.
                // Group repeated tick logs into candle-level episodes offline.
                if (IsGold(symbol) && ShouldSampleGoldHold(symbol))
                {
                    bool diagnosticBuy = bodyAtrBuy >= bodyAtrSell;
                    double directionalBody = diagnosticBuy ? bodyAtrBuy : bodyAtrSell;
                    double directionalEmaDistance = diagnosticBuy ? distFromEmaBuy : distFromEmaSell;
                    double directionalBreak = diagnosticBuy ? breakoutDistanceBuy : breakoutDistanceSell;
                    double directionalPosition = diagnosticBuy ? buyPositionInCandle : sellPositionInCandle;
                    bool regimeOk = diagnosticBuy ? buyRegimeAllowed : sellRegimeAllowed;
                    bool priceEma9Ok = diagnosticBuy ? priceAboveEma9 : priceBelowEma9;
                    bool priceEma21Ok = diagnosticBuy ? priceAboveEma21 : priceBelowEma21;
                    double slopeAtr = Math.Abs(ema9 - ema9Previous) / atr;
                    double spreadAtr = Math.Abs(ema9 - ema21) / atr;
                    double candleOpenToPrevEma9Atr = Math.Abs(liveCandle.open - ema9Previous) / atr;

                    // Report complete acceleration eligibility in addition to
                    // individual component checks. Synthetic candles have no
                    // reliable intrabar wick history, so don't require candle
                    // position when meaningful wick data is absent.
                    bool accelerationDirectionOk = diagnosticBuy
                        ? bullishLiveCandle && overallBullish && buyRegimeAllowed &&
                          ema9Rising && priceAboveEma9 && priceAboveEma21
                        : bearishLiveCandle && overallBearish && sellRegimeAllowed &&
                          ema9Falling && priceBelowEma9 && priceBelowEma21;
                    bool accelerationBodyOk = directionalBody >= 0.50;
                    bool accelerationMomentumOk = momentum >= 0.35;
                    bool accelerationEmaDistanceOk = directionalEmaDistance >= 0.20 &&
                        directionalEmaDistance <= 1.35;
                    bool accelerationSlopeOk = slopeAtr >= 0.05;
                    bool accelerationSpreadOk = spreadAtr >= 0.15;
                    bool accelerationBreakOk = directionalBreak >= -0.65 &&
                        directionalBreak <= 0.35;
                    bool accelerationCandlePositionOk = true; // No additional Gold position veto.
                    bool accelerationAllOk = accelerationDirectionOk &&
                        accelerationBodyOk && accelerationMomentumOk &&
                        accelerationEmaDistanceOk && accelerationSlopeOk &&
                        accelerationSpreadOk && accelerationBreakOk &&
                        accelerationCandlePositionOk;

                    // These snapshots are produced only after every entry path
                    // returned false, sampled once per 30 seconds per Gold symbol.
                    LogFile.Write(
                        $"[{symbol}] GOLD IMPULSE PATH DIAGNOSTIC | " +
                        $"direction={(diagnosticBuy ? "BUY" : "SELL")} | " +
                        $"sampleUtc={DateTime.UtcNow:O} | liveOpen={liveCandle.open:F5} | " +
                        $"bid={bid:F5} | ask={ask:F5} | recentHigh={recentHigh:F5} | recentLow={recentLow:F5} | " +
                        $"atr={atr:F5} | syntheticOrNoWicks={!goldHasMeaningfulWicks} | " +
                        $"accelerationAllOk={accelerationAllOk} | " +
                        $"accelerationDirectionOk={accelerationDirectionOk} | " +
                        $"accelerationEmaDistanceOk={accelerationEmaDistanceOk} | " +
                        $"accelerationCandlePositionOk={accelerationCandlePositionOk} | " +
                        $"bodyATR={directionalBody:F2} | momentum={momentum:F2} | " +
                        $"regimeOk={regimeOk} | priceEma9Ok={priceEma9Ok} | priceEma21Ok={priceEma21Ok} | " +
                        $"emaDistATR={directionalEmaDistance:F2} | breakATR={directionalBreak:F2} | " +
                        $"candlePosition={directionalPosition:F2} | emaSpreadATR={spreadAtr:F2} | " +
                        $"ema9SlopeATR={slopeAtr:F2} | openToPrevEma9ATR={candleOpenToPrevEma9Atr:F2} | " +
                        $"accelerationBodyOk={directionalBody >= 0.50} | " +
                        $"accelerationMomentumOk={momentum >= 0.35} | " +
                        $"accelerationSlopeOk={slopeAtr >= 0.05} | " +
                        $"accelerationSpreadOk={spreadAtr >= 0.15} | " +
                        $"accelerationBreakOk={directionalBreak >= -0.65 && directionalBreak <= 0.35} | " +
                        $"accelerationLocationOk={directionalEmaDistance >= 0.20 && directionalEmaDistance <= 1.35} | " +
                        $"transitionBodyOk={directionalBody >= profile.TransitionBody} | " +
                        $"transitionMomentumOk={momentum >= 0.50} | " +
                        $"transitionBreakOk={directionalBreak >= -1.10 && directionalBreak <= profile.TransitionBeyondBreakout} | " +
                        $"resumeBodyOk={directionalBody >= 0.50} | " +
                        $"resumeMomentumOk={momentum >= 0.60} | " +
                        $"resumeOpenRetestOk={candleOpenToPrevEma9Atr <= 0.35} | " +
                        $"escapeBodyOk={directionalBody >= profile.EscapeBody} | " +
                        $"escapeMomentumOk={momentum >= 0.55} | " +
                        $"escapeBreakOk={directionalBreak >= profile.EscapeMinBreakAtr && directionalBreak <= profile.EscapeMaxBreakAtr}"
                    );
                }

                if (bodyAtrBuy >= 0.45 ||
                    bodyAtrSell >= 0.45)
                {
                    LogFile.Write(
                        $"[{symbol}] Impulse not triggered | " +

                        $"buyBodyATR={bodyAtrBuy:F2} | " +

                        $"sellBodyATR={bodyAtrSell:F2} | " +

                        $"transitionReq=" +
                        $"{profile.TransitionBody:F2} | " +

                        $"earlyReq=" +
                        $"{profile.EarlyBody:F2} | " +

                        $"normalBuyReq=" +
                        $"{buyRequiredBody:F2} | " +

                        $"normalSellReq=" +
                        $"{sellRequiredBody:F2} | " +

                        $"escapeReq=" +
                        $"{profile.EscapeBody:F2} | " +

                        $"emaBuyDist=" +
                        $"{distFromEmaBuy:F2} | " +

                        $"emaSellDist=" +
                        $"{distFromEmaSell:F2} | " +

                        $"breakBuy=" +
                        $"{breakoutDistanceBuy:F2} | " +

                        $"breakSell=" +
                        $"{breakoutDistanceSell:F2} | " +

                        $"escapeBuy={escapeBuy} | " +

                        $"escapeSell={escapeSell} | " +

                        $"priceAboveEma9={priceAboveEma9} | " +

                        $"priceBelowEma9={priceBelowEma9} | " +

                        $"priceAboveEma21={priceAboveEma21} | " +

                        $"priceBelowEma21={priceBelowEma21} | " +

                        $"ema9Rising={ema9Rising} | " +

                        $"ema9Falling={ema9Falling} | " +

                        $"bullAligned={overallBullish} | " +

                        $"bearAligned={overallBearish} | " +

                        $"buyRegimeAllowed={buyRegimeAllowed} | " +

                        $"sellRegimeAllowed={sellRegimeAllowed} | " +

                        $"bullRegimeOverride={strongBullishRegimeOverride} | " +

                        $"bearRegimeOverride={strongBearishRegimeOverride}"
                    );
                }


                return false;
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"[{symbol}] LiveImpulseEntryEngine error: " +
                    $"{ex.Message}"
                );


                action =
                    TradeAction.Hold;


                return false;
            }
        }



        // ================================================================
        // CONFIRMED COUNTER-TREND REVERSAL
        // ================================================================
        //
        // This method deliberately does NOT change the public TryEnter
        // signature.  It remembers a possible reversal between calls.
        //
        // BUY sequence:
        //   bearish EMA9/EMA21 regime
        //      -> bullish impulse >= EarlyBody
        //      -> price above EMA9 + EMA9 rising
        //      -> EMA21 is reclaimed or approached
        //      -> controlled pullback
        //      -> pullback holds EMA9
        //      -> bullish rebound / follow-through
        //
        // SELL is the exact mirror image.
        //
        // The candidate is cancelled when the pullback becomes too deep,
        // EMA9 fails materially, or the setup becomes stale.

        private static bool TryConfirmedCounterTrendReversal(
            string symbol,
            MqlRates liveCandle,
            LiveImpulseProfile profile,
            double atr,
            double ema9,
            double ema21,
            bool overallBullish,
            bool overallBearish,
            bool ema9Rising,
            bool ema9Falling,
            bool bullishLiveCandle,
            bool bearishLiveCandle,
            bool priceAboveEma9,
            bool priceBelowEma9,
            bool priceAboveEma21,
            bool priceBelowEma21,
            double bodyAtrBuy,
            double bodyAtrSell,
            double distFromEmaBuy,
            double distFromEmaSell,
            double liveMid,
            double bid,
            double ask,
            out TradeAction action)
        {
            action =
                TradeAction.Hold;


            // ------------------------------------------------------------
            // Confirmation tuning
            // ------------------------------------------------------------
            // These are deliberately conservative.  They are confirmation
            // values, not primary impulse thresholds.

            double ema21ZoneAtr =
                0.15;

            // Gold needs a more meaningful pullback before a counter-trend
            // reversal can be confirmed.  Other asset classes keep the
            // existing behaviour.
            bool isGold =
                IsGold(symbol);

            double minimumPullbackAtr =
                isGold
                    ? 0.25
                    : 0.15;

            double maximumPullbackAtr =
                0.60;

            double ema9FailureToleranceAtr =
                0.12;

            // Gold also needs a slightly stronger renewed push after the
            // pullback.  This avoids confirming a reversal on a tiny 0.15-0.16
            // ATR reaction such as the XAUUSD trade from 25-Sep-2026.
            double reboundAtrRequired =
                isGold
                    ? 0.20
                    : 0.15;

            double minimumFollowThroughBodyAtr =
                0.20;

            // How far apart EMA9 and EMA21 still are at the time the
            // counter-trend reversal is trying to confirm.
            double emaSpreadAtr =
                Math.Abs(
                    ema9 - ema21
                ) / atr;

            // If Gold is still materially inside the OLD EMA regime, require
            // a stronger pullback structure before allowing the reversal.
            const double goldCounterTrendMaxOldRegimeSpreadAtr = 0.25;
            const double goldCounterTrendStrongPullbackAtr = 0.30;

            // ------------------------------------------------------------
            // FINAL EMA21 ENTRY CONFIRMATION
            // ------------------------------------------------------------
            // A counter-trend reversal is not allowed to enter merely because
            // EMA21 was touched earlier. At the actual entry moment price must:
            //
            // 1) reclaim EMA21, OR
            // 2) be very close to EMA21 with a stronger follow-through body.
            //
            // This keeps normal trend-aligned impulse logic unchanged while
            // preventing weak counter-trend entries directly underneath/above
            // a still-dominant EMA21.
            double ema21EntryProximityAtr =
                0.10;

            double strongNearEma21BodyAtr =
                0.35;

            TimeSpan maximumCandidateAge =
                TimeSpan.FromMinutes(75);


            // ------------------------------------------------------------
            // ARM A BUY REVERSAL CANDIDATE
            // ------------------------------------------------------------
            // Only arm against the OLD bearish EMA regime.
            // Do not buy yet.

            bool armBuy =
                overallBearish &&
                bullishLiveCandle &&
                bodyAtrBuy >= profile.EarlyBody &&
                ema9Rising &&
                priceAboveEma9 &&
                distFromEmaBuy <=
                    profile.TransitionMaxEmaDistance;


            // ------------------------------------------------------------
            // ARM A SELL REVERSAL CANDIDATE
            // ------------------------------------------------------------

            bool armSell =
                overallBullish &&
                bearishLiveCandle &&
                bodyAtrSell >= profile.EarlyBody &&
                ema9Falling &&
                priceBelowEma9 &&
                distFromEmaSell <=
                    profile.TransitionMaxEmaDistance;


            lock (ReversalLock)
            {
                ReversalCandidate candidate;

                bool hasCandidate =
                    ReversalCandidates.TryGetValue(
                        symbol,
                        out candidate
                    );


                // ========================================================
                // CREATE / RE-ARM CANDIDATE
                // ========================================================

                if (!hasCandidate)
                {
                    if (armBuy)
                    {
                        candidate =
                            new ReversalCandidate
                            {
                                IsBuy = true,
                                ArmedAtUtc = DateTime.UtcNow,
                                AnchorPrice = liveMid,
                                ExtremePrice = liveMid,
                                PullbackPrice = liveMid,
                                PullbackSeen = false,
                                TouchedEma21Zone =
                                    priceAboveEma21 ||
                                    Math.Abs(ema21 - liveMid) / atr <=
                                        ema21ZoneAtr
                            };

                        ReversalCandidates[symbol] =
                            candidate;

                        LogFile.Write(
                            $"[{symbol}] 🟡 BULL REVERSAL CANDIDATE ARMED | " +
                            $"bodyATR={bodyAtrBuy:F2} | " +
                            $"earlyReq={profile.EarlyBody:F2} | " +
                            $"price={liveMid:F5} | " +
                            $"ema9={ema9:F5} | " +
                            $"ema21={ema21:F5} | " +
                            $"ema9Rising={ema9Rising} | " +
                            $"NOTE=No trade yet; waiting for pullback + follow-through"
                        );
                    }
                    else if (armSell)
                    {
                        candidate =
                            new ReversalCandidate
                            {
                                IsBuy = false,
                                ArmedAtUtc = DateTime.UtcNow,
                                AnchorPrice = liveMid,
                                ExtremePrice = liveMid,
                                PullbackPrice = liveMid,
                                PullbackSeen = false,
                                TouchedEma21Zone =
                                    priceBelowEma21 ||
                                    Math.Abs(liveMid - ema21) / atr <=
                                        ema21ZoneAtr
                            };

                        ReversalCandidates[symbol] =
                            candidate;

                        LogFile.Write(
                            $"[{symbol}] 🟡 BEAR REVERSAL CANDIDATE ARMED | " +
                            $"bodyATR={bodyAtrSell:F2} | " +
                            $"earlyReq={profile.EarlyBody:F2} | " +
                            $"price={liveMid:F5} | " +
                            $"ema9={ema9:F5} | " +
                            $"ema21={ema21:F5} | " +
                            $"ema9Falling={ema9Falling} | " +
                            $"NOTE=No trade yet; waiting for pullback + follow-through"
                        );
                    }

                    return false;
                }


                // ========================================================
                // EXPIRE STALE CANDIDATE
                // ========================================================

                if (DateTime.UtcNow - candidate.ArmedAtUtc >
                    maximumCandidateAge)
                {
                    LogFile.Write(
                        $"[{symbol}] 🟤 REVERSAL CANDIDATE EXPIRED | " +
                        $"side={(candidate.IsBuy ? "BUY" : "SELL")} | " +
                        $"reason=confirmation did not arrive in time"
                    );

                    ReversalCandidates.Remove(
                        symbol
                    );

                    return false;
                }


                // ========================================================
                // BUY CANDIDATE
                // ========================================================

                if (candidate.IsBuy)
                {
                    candidate.TouchedEma21Zone =
                        candidate.TouchedEma21Zone ||
                        priceAboveEma21 ||
                        Math.Abs(ema21 - liveMid) / atr <=
                            ema21ZoneAtr;


                    candidate.ExtremePrice =
                        Math.Max(
                            candidate.ExtremePrice,
                            liveMid
                        );


                    double pullbackAtr =
                        (candidate.ExtremePrice - liveMid) /
                        atr;


                    // EMA9 materially failed: this was probably only a spike.
                    if (bid <
                        ema9 -
                        (ema9FailureToleranceAtr * atr))
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ BULL REVERSAL CANDIDATE CANCELLED | " +
                            $"price={liveMid:F5} | " +
                            $"ema9={ema9:F5} | " +
                            $"reason=pullback lost EMA9"
                        );

                        ReversalCandidates.Remove(
                            symbol
                        );

                        return false;
                    }


                    // Pullback became too deep before confirmation.
                    if (pullbackAtr >
                        maximumPullbackAtr)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ BULL REVERSAL CANDIDATE CANCELLED | " +
                            $"pullbackATR={pullbackAtr:F2} | " +
                            $"maxAllowed={maximumPullbackAtr:F2} | " +
                            $"reason=impulse gave back too much"
                        );

                        ReversalCandidates.Remove(
                            symbol
                        );

                        return false;
                    }


                    if (!candidate.PullbackSeen &&
                        pullbackAtr >= minimumPullbackAtr)
                    {
                        candidate.PullbackSeen =
                            true;

                        candidate.PullbackPrice =
                            liveMid;

                        LogFile.Write(
                            $"[{symbol}] 🟠 BULL REVERSAL PULLBACK SEEN | " +
                            $"peak={candidate.ExtremePrice:F5} | " +
                            $"pullbackPrice={liveMid:F5} | " +
                            $"pullbackATR={pullbackAtr:F2} | " +
                            $"ema9Held=True"
                        );
                    }


                    if (candidate.PullbackSeen)
                    {
                        candidate.PullbackPrice =
                            Math.Min(
                                candidate.PullbackPrice,
                                liveMid
                            );


                        double reboundAtr =
                            (liveMid - candidate.PullbackPrice) /
                            atr;


                        // Preserve the actual depth of the completed pullback.
                        // This is different from reboundAtr: pullbackDepth tells
                        // us whether price genuinely reset before trying again.
                        double buyPullbackDepthAtr =
                            (candidate.ExtremePrice -
                             candidate.PullbackPrice) /
                            atr;


                        double buyDistanceBelowEma21Atr =
                            Math.Max(
                                0.0,
                                ema21 - liveMid
                            ) / atr;


                        bool buyEma21EntryConfirmed =
                            priceAboveEma21 ||
                            (
                                buyDistanceBelowEma21Atr <=
                                    ema21EntryProximityAtr &&
                                bullishLiveCandle &&
                                bodyAtrBuy >=
                                    strongNearEma21BodyAtr
                            );


                        bool goldCounterTrendBuyConfirmed =
                            !isGold ||
                            overallBullish ||
                            emaSpreadAtr <=
                                goldCounterTrendMaxOldRegimeSpreadAtr ||
                            (
                                buyPullbackDepthAtr >=
                                    goldCounterTrendStrongPullbackAtr &&
                                reboundAtr >=
                                    reboundAtrRequired
                            );


                        bool followThrough =
                            candidate.TouchedEma21Zone &&
                            buyEma21EntryConfirmed &&
                            bullishLiveCandle &&
                            bodyAtrBuy >=
                                minimumFollowThroughBodyAtr &&
                            reboundAtr >=
                                reboundAtrRequired &&
                            priceAboveEma9 &&
                            ema9Rising &&
                            distFromEmaBuy <=
                                profile.TransitionMaxEmaDistance &&
                            goldCounterTrendBuyConfirmed;


                        if (followThrough)
                        {
                            action =
                                TradeAction.EnterBuy;

                            LogFile.Write(
                                $"[{symbol}] ✅ CONFIRMED REVERSAL BUY | " +
                                $"armedAt={candidate.AnchorPrice:F5} | " +
                                $"peak={candidate.ExtremePrice:F5} | " +
                                $"pullbackLow={candidate.PullbackPrice:F5} | " +
                                $"price={liveMid:F5} | " +
                                $"reboundATR={reboundAtr:F2} | " +
                                $"pullbackDepthATR={buyPullbackDepthAtr:F2} | " +
                                $"emaSpreadATR={emaSpreadAtr:F2} | " +
                                $"goldCounterTrendConfirmed={goldCounterTrendBuyConfirmed} | " +
                                $"bodyATR={bodyAtrBuy:F2} | " +
                                $"ema9Rising={ema9Rising} | " +
                                $"ema21ZoneSeen={candidate.TouchedEma21Zone} | " +
                                $"priceAboveEma21={priceAboveEma21} | " +
                                $"distBelowEma21ATR={buyDistanceBelowEma21Atr:F2} | " +
                                $"ema21EntryConfirmed={buyEma21EntryConfirmed} | " +
                                $"reason=impulse + controlled pullback + bullish follow-through + EMA21 confirmation"
                            );

                            ReversalCandidates.Remove(
                                symbol
                            );

                            return true;
                        }
                    }


                    ReversalCandidates[symbol] =
                        candidate;

                    return false;
                }


                // ========================================================
                // SELL CANDIDATE
                // ========================================================

                candidate.TouchedEma21Zone =
                    candidate.TouchedEma21Zone ||
                    priceBelowEma21 ||
                    Math.Abs(liveMid - ema21) / atr <=
                        ema21ZoneAtr;


                candidate.ExtremePrice =
                    Math.Min(
                        candidate.ExtremePrice,
                        liveMid
                    );


                double sellPullbackAtr =
                    (liveMid - candidate.ExtremePrice) /
                    atr;


                // EMA9 materially failed in the opposite direction.
                if (ask >
                    ema9 +
                    (ema9FailureToleranceAtr * atr))
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ BEAR REVERSAL CANDIDATE CANCELLED | " +
                        $"price={liveMid:F5} | " +
                        $"ema9={ema9:F5} | " +
                        $"reason=pullback lost EMA9"
                    );

                    ReversalCandidates.Remove(
                        symbol
                    );

                    return false;
                }


                if (sellPullbackAtr >
                    maximumPullbackAtr)
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ BEAR REVERSAL CANDIDATE CANCELLED | " +
                        $"pullbackATR={sellPullbackAtr:F2} | " +
                        $"maxAllowed={maximumPullbackAtr:F2} | " +
                        $"reason=impulse gave back too much"
                    );

                    ReversalCandidates.Remove(
                        symbol
                    );

                    return false;
                }


                if (!candidate.PullbackSeen &&
                    sellPullbackAtr >=
                        minimumPullbackAtr)
                {
                    candidate.PullbackSeen =
                        true;

                    candidate.PullbackPrice =
                        liveMid;

                    LogFile.Write(
                        $"[{symbol}] 🟠 BEAR REVERSAL PULLBACK SEEN | " +
                        $"trough={candidate.ExtremePrice:F5} | " +
                        $"pullbackPrice={liveMid:F5} | " +
                        $"pullbackATR={sellPullbackAtr:F2} | " +
                        $"ema9Held=True"
                    );
                }


                if (candidate.PullbackSeen)
                {
                    candidate.PullbackPrice =
                        Math.Max(
                            candidate.PullbackPrice,
                            liveMid
                        );


                    double sellReboundAtr =
                        (candidate.PullbackPrice - liveMid) /
                        atr;


                    // Preserve the actual depth of the completed pullback.
                    // A shallow reaction should not be enough to confirm a
                    // Gold reversal while the old EMA regime is still wide.
                    double sellPullbackDepthAtr =
                        (candidate.PullbackPrice -
                         candidate.ExtremePrice) /
                        atr;


                    double sellDistanceAboveEma21Atr =
                        Math.Max(
                            0.0,
                            liveMid - ema21
                        ) / atr;


                    bool sellEma21EntryConfirmed =
                        priceBelowEma21 ||
                        (
                            sellDistanceAboveEma21Atr <=
                                ema21EntryProximityAtr &&
                            bearishLiveCandle &&
                            bodyAtrSell >=
                                strongNearEma21BodyAtr
                        );


                    bool goldCounterTrendSellConfirmed =
                        !isGold ||
                        overallBearish ||
                        emaSpreadAtr <=
                            goldCounterTrendMaxOldRegimeSpreadAtr ||
                        (
                            sellPullbackDepthAtr >=
                                goldCounterTrendStrongPullbackAtr &&
                            sellReboundAtr >=
                                reboundAtrRequired
                        );


                    bool followThrough =
                        candidate.TouchedEma21Zone &&
                        sellEma21EntryConfirmed &&
                        bearishLiveCandle &&
                        bodyAtrSell >=
                            minimumFollowThroughBodyAtr &&
                        sellReboundAtr >=
                            reboundAtrRequired &&
                        priceBelowEma9 &&
                        ema9Falling &&
                        distFromEmaSell <=
                            profile.TransitionMaxEmaDistance &&
                        goldCounterTrendSellConfirmed;


                    if (followThrough)
                    {
                        action =
                            TradeAction.EnterSell;

                        LogFile.Write(
                            $"[{symbol}] ✅ CONFIRMED REVERSAL SELL | " +
                            $"armedAt={candidate.AnchorPrice:F5} | " +
                            $"trough={candidate.ExtremePrice:F5} | " +
                            $"pullbackHigh={candidate.PullbackPrice:F5} | " +
                            $"price={liveMid:F5} | " +
                            $"reboundATR={sellReboundAtr:F2} | " +
                            $"pullbackDepthATR={sellPullbackDepthAtr:F2} | " +
                            $"emaSpreadATR={emaSpreadAtr:F2} | " +
                            $"goldCounterTrendConfirmed={goldCounterTrendSellConfirmed} | " +
                            $"bodyATR={bodyAtrSell:F2} | " +
                            $"ema9Falling={ema9Falling} | " +
                            $"ema21ZoneSeen={candidate.TouchedEma21Zone} | " +
                            $"priceBelowEma21={priceBelowEma21} | " +
                            $"distAboveEma21ATR={sellDistanceAboveEma21Atr:F2} | " +
                            $"ema21EntryConfirmed={sellEma21EntryConfirmed} | " +
                            $"reason=impulse + controlled pullback + bearish follow-through + EMA21 confirmation"
                        );

                        ReversalCandidates.Remove(
                            symbol
                        );

                        return true;
                    }
                }


                ReversalCandidates[symbol] =
                    candidate;

                return false;
            }
        }


        // ================================================================
        // ASSET-SPECIFIC WICK REJECTION THRESHOLD
        // ================================================================
        // A higher value means more wick/rejection is tolerated before a
        // fresh impulse entry is blocked.
        //
        // Gold / crypto are intentionally given more tolerance because they
        // naturally print larger intrabar wicks than FX and indices.
        //
        // These values are deliberately isolated here so they can be tuned
        // later without changing the entry structure.
        // ================================================================

        private static double GetRejectionWickThreshold(
            string symbol)
        {
            symbol =
                symbol?.ToUpperInvariant() ?? "";


            if (IsGold(symbol))
            {
                return 0.35;
            }


            if (IsCrypto(symbol))
            {
                return 0.35;
            }


            if (IsIndex(symbol))
            {
                return 0.30;
            }


            if (IsFx(symbol))
            {
                return 0.30;
            }


            return 0.32;
        }


        // ================================================================
        // ASSET-SPECIFIC PROFILE
        // ================================================================

        private static LiveImpulseProfile GetProfile(
            string symbol)
        {
            symbol =
                symbol.ToUpperInvariant();


            // ============================================================
            // GOLD / METALS
            // ============================================================

            if (IsGold(symbol))
            {
                return new LiveImpulseProfile
                {
                    BodyThreshold = 1.00,

                    CounterTrendBoost = 0.35,


                    EarlyBody = 0.70,

                    EarlyDistance = 0.12,

                    MinEmaDistance = 0.10,

                    MaxEmaDistance = 1.25,

                    EarlyBreakAllowance = 0.00,


                    TransitionBody = 0.75,

                    TransitionMaxEmaDistance = 1.20,

                    TransitionBeforeBreakout = 0.35,

                    TransitionBeyondBreakout = 0.50,


                    NormalMaxBeyondBreakout = 0.60,


                    EscapeBody = 1.45,

                    EscapeMinBreakAtr = 0.05,

                    EscapeMaxBreakAtr = 0.85,

                    EscapeMinEmaDistanceAtr = 0.30,

                    EscapeMaxEmaDistanceAtr = 2.20
                };
            }


            // ============================================================
            // CRYPTO
            // ============================================================

            if (IsCrypto(symbol))
            {
                return new LiveImpulseProfile
                {
                    BodyThreshold = 0.85,

                    CounterTrendBoost = 0.25,


                    EarlyBody = 0.70,

                    EarlyDistance = 0.40,

                    MinEmaDistance = 0.12,

                    MaxEmaDistance = 2.50,

                    EarlyBreakAllowance = 0.12,


                    TransitionBody = 0.75,

                    TransitionMaxEmaDistance = 1.75,

                    TransitionBeforeBreakout = 0.30,

                    TransitionBeyondBreakout = 0.60,


                    NormalMaxBeyondBreakout = 0.80,


                    EscapeBody = 1.50,

                    EscapeMinBreakAtr = 0.08,

                    EscapeMaxBreakAtr = 1.00,

                    EscapeMinEmaDistanceAtr = 0.35,

                    EscapeMaxEmaDistanceAtr = 2.75
                };
            }


            // ============================================================
            // INDICES
            // ============================================================

            if (IsIndex(symbol))
            {
                return new LiveImpulseProfile
                {
                    BodyThreshold = 0.90,

                    CounterTrendBoost = 0.30,


                    EarlyBody = 0.50,

                    EarlyDistance = 0.60,

                    MinEmaDistance = 0.10,

                    MaxEmaDistance = 2.00,

                    EarlyBreakAllowance = 0.12,


                    TransitionBody = 0.60,

                    TransitionMaxEmaDistance = 1.50,

                    TransitionBeforeBreakout = 0.40,

                    TransitionBeyondBreakout = 0.60,


                    NormalMaxBeyondBreakout = 0.70,


                    EscapeBody = 1.40,

                    EscapeMinBreakAtr = 0.08,

                    EscapeMaxBreakAtr = 0.90,

                    EscapeMinEmaDistanceAtr = 0.30,

                    EscapeMaxEmaDistanceAtr = 2.25
                };
            }


            // ============================================================
            // FOREX
            // ============================================================

            if (IsFx(symbol))
            {
                return new LiveImpulseProfile
                {
                    BodyThreshold = 0.60,

                    CounterTrendBoost = 0.20,


                    EarlyBody = 0.50,

                    EarlyDistance = 0.50,

                    MinEmaDistance = 0.06,

                    MaxEmaDistance = 1.60,

                    EarlyBreakAllowance = 0.08,


                    TransitionBody = 0.55,

                    TransitionMaxEmaDistance = 1.20,

                    TransitionBeforeBreakout = 0.25,

                    TransitionBeyondBreakout = 0.40,


                    NormalMaxBeyondBreakout = 0.50,


                    EscapeBody = 1.25,

                    EscapeMinBreakAtr = 0.06,

                    EscapeMaxBreakAtr = 0.70,

                    EscapeMinEmaDistanceAtr = 0.20,

                    EscapeMaxEmaDistanceAtr = 1.75
                };
            }


            // ============================================================
            // DEFAULT
            // ============================================================

            return new LiveImpulseProfile
            {
                BodyThreshold = 0.90,

                CounterTrendBoost = 0.25,


                EarlyBody = 0.60,

                EarlyDistance = 0.50,

                MinEmaDistance = 0.10,

                MaxEmaDistance = 2.00,

                EarlyBreakAllowance = 0.10,


                TransitionBody = 0.65,

                TransitionMaxEmaDistance = 1.50,

                TransitionBeforeBreakout = 0.30,

                TransitionBeyondBreakout = 0.50,


                NormalMaxBeyondBreakout = 0.60,


                EscapeBody = 1.45,

                EscapeMinBreakAtr = 0.08,

                EscapeMaxBreakAtr = 0.85,

                EscapeMinEmaDistanceAtr = 0.30,

                EscapeMaxEmaDistanceAtr = 2.20
            };
        }


        // ================================================================
        // GOLD / METALS
        // ================================================================

        private static bool IsGold(
            string symbol)
        {
            return
                symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER");
        }


        // ================================================================
        // CRYPTO
        // ================================================================

        private static bool IsCrypto(
            string symbol)
        {
            return
                symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("DOGE") ||
                symbol.Contains("LTC");
        }


        // ================================================================
        // INDICES
        // ================================================================

        private static bool IsIndex(
            string symbol)
        {
            return
                symbol.Contains("US100") ||
                symbol.Contains("NAS") ||
                symbol.Contains("SPX") ||
                symbol.Contains("US500") ||
                symbol.Contains("US30") ||
                symbol.Contains("DJI") ||
                symbol.Contains("GER40") ||
                symbol.Contains("UK100") ||
                symbol.Contains("JP225") ||
                symbol.Contains("INDEX") ||
                symbol.Contains(".CASH");
        }


        // ================================================================
        // FOREX
        // ================================================================

        private static bool IsFx(
            string symbol)
        {
            if (symbol.Length < 6)
                return false;


            string[] ccy =
            {
    "USD",
    "EUR",
    "GBP",
    "JPY",
    "AUD",
    "NZD",
    "CAD",
    "CHF"
};


            foreach (var baseCcy in ccy)
            {
                foreach (var quoteCcy in ccy)
                {
                    if (baseCcy ==
                        quoteCcy)
                    {
                        continue;
                    }


                    if (symbol.StartsWith(baseCcy) &&
                        symbol.Contains(quoteCcy))
                    {
                        return true;
                    }
                }
            }


            return false;
        }
    }
}
