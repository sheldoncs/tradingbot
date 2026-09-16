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

                if (TryConfirmedCounterTrendReversal(
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


                bool extendedImpulseBuy =
                    bullishLiveCandle &&
                    buyRegimeAllowed &&
                    bodyAtrBuy >= chaseBodyThreshold &&
                    (
                        distFromEmaBuy >= chaseEmaDistance ||
                        breakoutDistanceBuy >= chaseBreakoutDistance
                    );


                bool extendedImpulseSell =
                    bearishLiveCandle &&
                    sellRegimeAllowed &&
                    bodyAtrSell >= chaseBodyThreshold &&
                    (
                        distFromEmaSell >= chaseEmaDistance ||
                        breakoutDistanceSell >= chaseBreakoutDistance
                    );


                if (extendedImpulseBuy)
                {
                    LogFile.Write(
                        $"[{symbol}] ⛔ IMPULSE BUY BLOCKED - EXTENDED / DO NOT CHASE | " +
                        $"bodyATR={bodyAtrBuy:F2} | " +
                        $"chaseBodyReq={chaseBodyThreshold:F2} | " +
                        $"emaDist={distFromEmaBuy:F2} | " +
                        $"maxChaseEmaDist={chaseEmaDistance:F2} | " +
                        $"breakATR={breakoutDistanceBuy:F2} | " +
                        $"maxChaseBreak={chaseBreakoutDistance:F2} | " +
                        $"NOTE=Momentum is strong but price is already extended; wait for pullback/retest"
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
                        $"breakATR={breakoutDistanceSell:F2} | " +
                        $"maxChaseBreak={chaseBreakoutDistance:F2} | " +
                        $"NOTE=Momentum is strong but price is already extended; wait for pullback/retest"
                    );

                    return false;
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
                // STRUCTURAL ESCAPE BUY
                //
                // Strong structural move, but direction must still agree
                // with the primary EMA9 / EMA21 regime.
                // ========================================================

                bool escapeBuy =
                    bullishLiveCandle &&

                    buyRegimeAllowed &&

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
                // TRANSITION IMPULSE BUY
                // ========================================================

                bool nearOrBreakingHigh =
                    breakoutDistanceBuy >=
                        -profile.TransitionBeforeBreakout &&

                    breakoutDistanceBuy <=
                        profile.TransitionBeyondBreakout;


                bool transitionImpulseBuy =
                    bullishLiveCandle &&

                    buyRegimeAllowed &&

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

                        $"emaDist={distFromEmaBuy:F2} | " +

                        $"breakDist=" +
                        $"{breakoutDistanceBuy:F2} | " +

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

                bool nearOrBreakingLow =
                    breakoutDistanceSell >=
                        -profile.TransitionBeforeBreakout &&

                    breakoutDistanceSell <=
                        profile.TransitionBeyondBreakout;


                bool transitionImpulseSell =
                    bearishLiveCandle &&

                    sellRegimeAllowed &&

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

                        $"emaDist={distFromEmaSell:F2} | " +

                        $"breakDist=" +
                        $"{breakoutDistanceSell:F2} | " +

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

            double minimumPullbackAtr =
                0.15;

            double maximumPullbackAtr =
                0.60;

            double ema9FailureToleranceAtr =
                0.12;

            double reboundAtrRequired =
                0.15;

            double minimumFollowThroughBodyAtr =
                0.20;

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
                                profile.TransitionMaxEmaDistance;


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
                            profile.TransitionMaxEmaDistance;


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

                    EarlyDistance = 0.35,

                    MinEmaDistance = 0.10,

                    MaxEmaDistance = 1.25,

                    EarlyBreakAllowance = 0.10,


                    TransitionBody = 0.65,

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
