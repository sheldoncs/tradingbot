using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Services
{
    public static class BreakoutContinuationEngine
    {
        // ==================================================
        // PROFILE
        // ==================================================

        private class Profile
        {
            public int StructureLookback { get; set; }

            public double BreakBufferAtr { get; set; }

            public double MaxDistanceFromEma9Atr { get; set; }

            public double MaxDistanceFromEma21Atr { get; set; }

            public double MaxBreakoutDistanceAtr { get; set; }

            public double MinMomentum { get; set; }

            public double MinEmaSpreadAtr { get; set; }

            public int HoldSeconds { get; set; }


            // ==============================================
            // INSTANT / EARLY BREAKOUT QUALITY
            // ==============================================

            public double InstantMinBodyAtr { get; set; }

            public double InstantMinEmaDistanceAtr { get; set; }

            public double InstantMaxEmaDistanceAtr { get; set; }

            public double ChoppyCloseDistanceAtr { get; set; }

            public double ChoppyRangeAtr { get; set; }
        }


        // ==================================================
        // LIVE BREAK STATE
        // ==================================================

        private class LiveBreakState
        {
            public bool BullBreakDetected;

            public bool BearBreakDetected;

            public DateTime FirstBreakTimeUtc;
        }


        private static readonly Dictionary<string, LiveBreakState>
            _states = new();


        private static readonly Dictionary<string, bool>
            WaitingForPullback = new();


        // ==================================================
        // MAIN ENTRY METHOD
        // ==================================================

        public static bool ShouldEnterLive(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double momentum,
            double bid,
            double ask,
            MarketState marketState,
            out TradeAction action)
        {
            action = TradeAction.Hold;


            // ==================================================
            // 1. BASIC VALIDATION
            // ==================================================

            if (string.IsNullOrWhiteSpace(symbol) ||
                rates == null ||
                emaFast == null ||
                emaSlow == null ||
                rates.Length < 20 ||
                emaFast.Length < 3 ||
                emaSlow.Length < 3 ||
                atr <= 0)
            {
                return false;
            }


            string s =
                NormalizeSymbol(symbol);

            Profile profile =
                GetProfile(s, marketState);


            if (!_states.ContainsKey(s))
            {
                _states[s] =
                    new LiveBreakState();
            }


            LiveBreakState state =
                _states[s];


            // ==================================================
            // 2. CLOSED CANDLES
            // ==================================================

            MqlRates c0 =
                rates[^1];

            MqlRates c1 =
                rates[^2];


            // ==================================================
            // 3. EMA STRUCTURE
            // ==================================================

            double ema9 =
                emaFast[^1];

            double ema21 =
                emaSlow[^1];


            bool trendUp =
                ema9 > ema21 &&
                emaFast[^1] > emaFast[^2];


            bool trendDown =
                ema9 < ema21 &&
                emaFast[^1] < emaFast[^2];


            double emaSpreadAtr =
                Math.Abs(ema9 - ema21) / atr;


            bool emaSpreadTooSmall =
                emaSpreadAtr <
                profile.MinEmaSpreadAtr;


            // ==================================================
            // 4. CANDLE QUALITY
            // ==================================================

            double body =
                Math.Abs(
                    c0.close -
                    c0.open
                );


            double range =
                c0.high -
                c0.low;


            double bodyRatio =
                range > 0
                    ? body / range
                    : 0;


            double bodyAtrCheck =
                body / atr;


            bool strongImpulse =
                bodyAtrCheck >= 0.70;


            bool mediumImpulse =
                bodyAtrCheck >= 0.40;


            // ==================================================
            // 5. MOMENTUM BYPASS
            //
            // Gold gets a strong-candle bypass.
            //
            // Indices do NOT get the weak-medium-candle bypass.
            // This helps prevent US100 chop entries.
            // ==================================================

            bool allowMomentumBypass =
                IsGold(s) &&
                bodyAtrCheck > 1.10;


            bool allowWeakMomentumForEarly =
                !IsIndex(s) &&
                mediumImpulse &&
                bodyAtrCheck < 0.70;


            if (!allowWeakMomentumForEarly &&
                Math.Abs(momentum) <
                    profile.MinMomentum &&
                !allowMomentumBypass)
            {
                ResetState(s);

                LogFile.Write(
                    $"[{s}] Breakout blocked: weak momentum | " +
                    $"momentum={momentum:F2}, " +
                    $"min={profile.MinMomentum:F2}, " +
                    $"bodyAtr={bodyAtrCheck:F2}"
                );

                return false;
            }


            // ==================================================
            // 6. RECENT STRUCTURE
            // ==================================================

            int lookback =
                Math.Min(
                    profile.StructureLookback,
                    rates.Length - 2
                );


            double recentHigh =
                double.MinValue;


            double recentLow =
                double.MaxValue;


            for (int i = 2;
                 i <= lookback + 1;
                 i++)
            {
                recentHigh =
                    Math.Max(
                        recentHigh,
                        rates[^i].high
                    );


                recentLow =
                    Math.Min(
                        recentLow,
                        rates[^i].low
                    );
            }


            double buffer =
                atr *
                profile.BreakBufferAtr;


            double liveBuyPrice =
                ask;


            double liveSellPrice =
                bid;


            // ==================================================
            // 7. DIRECTIONAL CLOSED-CANDLE QUALITY
            // ==================================================

            bool strongBullBody =
                c0.close > c0.open &&
                body >= atr * 0.35 &&
                bodyRatio >= 0.45 &&
                bodyAtrCheck > 0.60;


            bool strongBearBody =
                c0.close < c0.open &&
                body >= atr * 0.35 &&
                bodyRatio >= 0.45 &&
                bodyAtrCheck > 0.60;


            // ==================================================
            // 8. NORMAL STRUCTURE BREAKOUT
            // ==================================================

            bool liveBullBreak =
                trendUp &&
                liveBuyPrice >
                    recentHigh + buffer;


            bool liveBearBreak =
                trendDown &&
                liveSellPrice <
                    recentLow - buffer;


            // ==================================================
            // 9. EARLY ESCAPE
            // ==================================================

            bool liveBullEscape =
                liveBuyPrice > c1.high &&
                liveBuyPrice > ema9 &&
                liveBuyPrice > ema21 &&
                strongBullBody;


            bool liveBearEscape =
                liveSellPrice < c1.low &&
                liveSellPrice < ema9 &&
                liveSellPrice < ema21 &&
                strongBearBody;


            // ==================================================
            // 10. EMA SPREAD CHECK
            // ==================================================

            if (emaSpreadTooSmall &&
                !liveBullEscape &&
                !liveBearEscape)
            {
                ResetState(s);

                LogFile.Write(
                    $"[{s}] Breakout blocked: EMA spread too small and no escape | " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}, " +
                    $"min={profile.MinEmaSpreadAtr:F2}, " +
                    $"bullEscape={liveBullEscape}, " +
                    $"bearEscape={liveBearEscape}"
                );

                return false;
            }


            // ==================================================
            // 11. TREND / ESCAPE VALIDATION
            // ==================================================

            if (!trendUp &&
                !trendDown &&
                !liveBullEscape &&
                !liveBearEscape)
            {
                ResetState(s);

                LogFile.Write(
                    $"[{s}] Breakout blocked: no trend / escape | " +
                    $"trendUp={trendUp}, " +
                    $"trendDown={trendDown}, " +
                    $"liveBid={liveSellPrice:F5}, " +
                    $"c1Low={c1.low:F5}, " +
                    $"bearEscape={liveBearEscape}, " +
                    $"liveAsk={liveBuyPrice:F5}, " +
                    $"c1High={c1.high:F5}, " +
                    $"bullEscape={liveBullEscape}"
                );

                return false;
            }


            // ==================================================
            // 12. EMA DISTANCES
            // ==================================================

            double distBuyFromEma9Atr =
                Math.Abs(
                    liveBuyPrice -
                    ema9
                ) / atr;


            double distSellFromEma9Atr =
                Math.Abs(
                    liveSellPrice -
                    ema9
                ) / atr;


            double distBuyFromEma21Atr =
                Math.Abs(
                    liveBuyPrice -
                    ema21
                ) / atr;


            double distSellFromEma21Atr =
                Math.Abs(
                    liveSellPrice -
                    ema21
                ) / atr;


            // ==================================================
            // 13. LATE / CHASE PROFILE
            // ==================================================

            double lateEmaDistance;

            double lateBreakDistance;


            if (IsGold(s))
            {
                lateEmaDistance = 0.70;

                lateBreakDistance = 0.28;
            }
            else if (IsCrypto(s))
            {
                if (s.Contains("BNB"))
                {
                    lateEmaDistance = 0.60;

                    lateBreakDistance = 0.25;
                }
                else if (s.Contains("SOL"))
                {
                    lateEmaDistance = 0.65;

                    lateBreakDistance = 0.30;
                }
                else
                {
                    lateEmaDistance = 0.75;

                    lateBreakDistance = 0.35;
                }
            }
            else if (IsIndex(s))
            {
                lateEmaDistance = 0.65;

                lateBreakDistance = 0.30;
            }
            else
            {
                // FX
                lateEmaDistance = 0.45;

                lateBreakDistance = 0.22;
            }


            // ==================================================
            // 14. LIVE BREAKOUT DISTANCE
            // ==================================================

            double liveBuyBreakDistanceAtr =
                (liveBuyPrice -
                 recentHigh) / atr;


            double liveSellBreakDistanceAtr =
                (recentLow -
                 liveSellPrice) / atr;


            bool lateChaseBuy =
                liveBuyPrice > recentHigh &&
                distBuyFromEma9Atr >=
                    lateEmaDistance &&
                liveBuyBreakDistanceAtr >=
                    lateBreakDistance;


            bool lateChaseSell =
                liveSellPrice < recentLow &&
                distSellFromEma9Atr >=
                    lateEmaDistance &&
                liveSellBreakDistanceAtr >=
                    lateBreakDistance;


            if (lateChaseBuy)
            {
                ResetState(s);

                WaitingForPullback[s] =
                    true;

                LogFile.Write(
                    $"[{s}] ❌ BUY blocked: late breakout chase | " +
                    $"emaDist={distBuyFromEma9Atr:F2}/{lateEmaDistance:F2}, " +
                    $"breakDist={liveBuyBreakDistanceAtr:F2}/{lateBreakDistance:F2}, " +
                    $"price={liveBuyPrice:F5}, " +
                    $"recentHigh={recentHigh:F5}"
                );

                return false;
            }


            if (lateChaseSell)
            {
                ResetState(s);

                WaitingForPullback[s] =
                    true;

                LogFile.Write(
                    $"[{s}] ❌ SELL blocked: late breakout chase | " +
                    $"emaDist={distSellFromEma9Atr:F2}/{lateEmaDistance:F2}, " +
                    $"breakDist={liveSellBreakDistanceAtr:F2}/{lateBreakDistance:F2}, " +
                    $"price={liveSellPrice:F5}, " +
                    $"recentLow={recentLow:F5}"
                );

                return false;
            }


            // ==================================================
            // 15. PRE-BREAKOUT SETTINGS
            // ==================================================

            double maxPreBreakoutDist =
                IsCrypto(s) ? 0.40 :
                IsIndex(s) ? 0.60 :
                IsGold(s) ? 0.50 :
                0.50;


            double maxEmaDist =
                IsCrypto(s) ? 0.60 :
                IsIndex(s) ? 0.80 :
                IsGold(s) ? 0.70 :
                0.70;


            double minBodyAtr =
                IsCrypto(s) ? 0.70 :
                IsIndex(s) ? 0.55 :
                IsGold(s) ? 0.60 :
                0.50;


            double distToBreakoutAtr =
                (recentHigh -
                 liveBuyPrice) / atr;


            double distToBreakdownAtr =
                (liveSellPrice -
                 recentLow) / atr;


            double minMomentum =
                IsCrypto(s) ? 4.00 :
                IsIndex(s) ? 5.00 :
                IsGold(s) ? 3.50 :
                3.00;


            bool strongMomentum =
                momentum >= minMomentum;


            // ==================================================
            // 16. PRE-BREAKOUT BUY
            // ==================================================

            bool preBreakoutBuy =
                trendUp &&
                liveBuyPrice > ema9 &&
                liveBuyPrice > ema21 &&
                distToBreakoutAtr > 0 &&
                distToBreakoutAtr <
                    maxPreBreakoutDist &&
                bodyAtrCheck > minBodyAtr &&
                distBuyFromEma9Atr <
                    maxEmaDist &&
                strongMomentum;


            if (preBreakoutBuy)
            {
                bool allowPreBreakout =
                    strongMomentum &&
                    bodyAtrCheck > 1.00;


                if (!allowPreBreakout)
                {
                    LogFile.Write(
                        $"[{s}] ❌ Blocked: weak pre-breakout BUY | " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"momentum={momentum:F2}"
                    );

                    return false;
                }


                action =
                    TradeAction.EnterBuy;


                LogFile.Write(
                    $"[{s}] ⚡ PRE-BREAKOUT BUY | " +
                    $"dist={distToBreakoutAtr:F2}, " +
                    $"bodyAtr={bodyAtrCheck:F2}"
                );


                return true;
            }


            // ==================================================
            // 17. PRE-BREAKOUT SELL
            // ==================================================

            bool preBreakoutSell =
                trendDown &&
                liveSellPrice < ema9 &&
                liveSellPrice < ema21 &&
                distToBreakdownAtr > 0 &&
                distToBreakdownAtr <
                    maxPreBreakoutDist &&
                bodyAtrCheck > minBodyAtr &&
                distSellFromEma9Atr <
                    maxEmaDist &&
                strongMomentum;


            if (preBreakoutSell)
            {
                bool allowPreBreakout =
                    strongMomentum &&
                    bodyAtrCheck > 1.00;


                if (!allowPreBreakout)
                {
                    LogFile.Write(
                        $"[{s}] ❌ Blocked: weak pre-breakout SELL | " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"momentum={momentum:F2}"
                    );

                    return false;
                }


                action =
                    TradeAction.EnterSell;


                LogFile.Write(
                    $"[{s}] ⚡ PRE-BREAKOUT SELL | " +
                    $"dist={distToBreakdownAtr:F2}, " +
                    $"bodyAtr={bodyAtrCheck:F2}"
                );


                return true;
            }


            // ==================================================
            // 18. CHOP / QUALITY DETECTION
            //
            // Especially important for indices.
            // ==================================================

            bool choppySetup =
                IsChoppyForInstantEntry(
                    s,
                    rates,
                    atr,
                    profile
                );


            bool instantBuyQuality =
                bodyAtrCheck >=
                    profile.InstantMinBodyAtr &&

                distBuyFromEma9Atr >=
                    profile.InstantMinEmaDistanceAtr &&

                distBuyFromEma9Atr <=
                    profile.InstantMaxEmaDistanceAtr &&

                !choppySetup;


            bool instantSellQuality =
                bodyAtrCheck >=
                    profile.InstantMinBodyAtr &&

                distSellFromEma9Atr >=
                    profile.InstantMinEmaDistanceAtr &&

                distSellFromEma9Atr <=
                    profile.InstantMaxEmaDistanceAtr &&

                !choppySetup;


            // ==================================================
            // 19. BUY BREAKOUT
            // ==================================================

            if (liveBullBreak ||
                liveBullEscape)
            {
                double breakoutDistance =
                    liveBullBreak
                        ? (liveBuyPrice -
                           recentHigh) / atr
                        : (liveBuyPrice -
                           c1.high) / atr;


                double maxMoveAtr =
                    IsGold(s) ? 1.50 :
                    IsCrypto(s) ? 2.00 :
                    IsIndex(s) ? 1.50 :
                    IsFx(s) ? 1.20 :
                    1.40;


                double moveFromRecentLowAtr =
                    (liveBuyPrice -
                     recentLow) / atr;


                double emaDist =
                    Math.Abs(
                        liveBuyPrice -
                        ema9
                    ) / atr;


                bool trendContinuation =
                    Math.Abs(momentum) > 4 &&
                    bodyAtrCheck > 0.50 &&
                    emaDist > 0.15;


                bool lateImpulseContinuation =
                    bodyAtrCheck > 1.00 &&
                    Math.Abs(momentum) > 6;


                bool allowExtendedMove =
                    strongImpulse ||
                    trendContinuation ||
                    lateImpulseContinuation;


                if (moveFromRecentLowAtr >
                        maxMoveAtr &&
                    !allowExtendedMove)
                {
                    ResetState(s);

                    WaitingForPullback[s] =
                        true;

                    LogFile.Write(
                        $"[{s}] ❌ BUY blocked: extended move | " +
                        $"moveATR={moveFromRecentLowAtr:F2}, " +
                        $"max={maxMoveAtr:F2}"
                    );

                    return false;
                }


                if (distBuyFromEma9Atr >
                    profile.MaxDistanceFromEma9Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] BUY blocked: too far from EMA9 | " +
                        $"dist={distBuyFromEma9Atr:F2}, " +
                        $"max={profile.MaxDistanceFromEma9Atr:F2}"
                    );

                    return false;
                }


                if (distBuyFromEma21Atr >
                    profile.MaxDistanceFromEma21Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] BUY blocked: too far from EMA21 | " +
                        $"dist={distBuyFromEma21Atr:F2}, " +
                        $"max={profile.MaxDistanceFromEma21Atr:F2}"
                    );

                    return false;
                }


                if (breakoutDistance >
                    profile.MaxBreakoutDistanceAtr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] BUY blocked: chase breakout | " +
                        $"breakoutDistanceAtr={breakoutDistance:F2}, " +
                        $"max={profile.MaxBreakoutDistanceAtr:F2}"
                    );

                    return false;
                }


                double maxEarlyDistance =
                    IsGold(s) ? 0.70 :
                    IsCrypto(s) ? 0.80 :
                    IsIndex(s) ? 0.60 :
                    IsFx(s) ? 0.40 :
                    0.50;


                if (distBuyFromEma9Atr >
                    maxEarlyDistance)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ❌ Early breakout blocked: too extended | " +
                        $"emaDist={distBuyFromEma9Atr:F2}, " +
                        $"max={maxEarlyDistance:F2}"
                    );

                    return false;
                }


                // ==================================================
                // ULTRA MOMENTUM BUY
                // ==================================================

                bool ultraMomentum =
                    bodyAtrCheck > 0.90;


                if (liveBullEscape &&
                    ultraMomentum &&
                    !choppySetup)
                {
                    action =
                        TradeAction.EnterBuy;


                    ResetState(s);


                    LogFile.Write(
                        $"[{s}] ⚡ ULTRA MOMENTUM BUY | " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"emaDist={distBuyFromEma9Atr:F2}, " +
                        $"choppy={choppySetup}"
                    );


                    return true;
                }


                // ==================================================
                // QUALITY INSTANT BUY
                // ==================================================

                if (liveBullEscape &&
                    instantBuyQuality)
                {
                    action =
                        TradeAction.EnterBuy;


                    ResetState(s);


                    LogFile.Write(
                        $"[{s}] ⚡ INSTANT BUY (quality breakout) | " +
                        $"price={liveBuyPrice:F5}, " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"emaDist={distBuyFromEma9Atr:F2}, " +
                        $"choppy={choppySetup}"
                    );


                    return true;
                }


                if (liveBullEscape &&
                    !instantBuyQuality)
                {
                    LogFile.Write(
                        $"[{s}] ❌ INSTANT BUY blocked: weak/choppy breakout | " +
                        $"bodyAtr={bodyAtrCheck:F2}/{profile.InstantMinBodyAtr:F2}, " +
                        $"emaDist={distBuyFromEma9Atr:F2}/" +
                        $"{profile.InstantMinEmaDistanceAtr:F2}-" +
                        $"{profile.InstantMaxEmaDistanceAtr:F2}, " +
                        $"choppy={choppySetup}"
                    );
                }


                // ==================================================
                // NORMAL DELAYED BUY
                // ==================================================

                if (!state.BullBreakDetected)
                {
                    state.BullBreakDetected =
                        true;


                    state.BearBreakDetected =
                        false;


                    state.FirstBreakTimeUtc =
                        DateTime.UtcNow;


                    LogFile.Write(
                        $"[{s}] BUY breakout detected | " +
                        $"normalBreak={liveBullBreak}, " +
                        $"escape={liveBullEscape}, " +
                        $"hold={profile.HoldSeconds}s, " +
                        $"price={liveBuyPrice:F5}, " +
                        $"recentHigh={recentHigh:F5}, " +
                        $"c1High={c1.high:F5}, " +
                        $"emaSpreadAtr={emaSpreadAtr:F2}, " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"bodyRatio={bodyRatio:F2}, " +
                        $"breakoutDistanceAtr={breakoutDistance:F2}, " +
                        $"maxBreakoutDistanceAtr={profile.MaxBreakoutDistanceAtr:F2}"
                    );


                    return false;
                }


                double heldFor =
                    (DateTime.UtcNow -
                     state.FirstBreakTimeUtc)
                    .TotalSeconds;


                if (heldFor >=
                    profile.HoldSeconds)
                {
                    if (liveBuyPrice < ema9)
                    {
                        ResetState(s);

                        LogFile.Write(
                            $"[{s}] BUY blocked after hold: " +
                            $"price fell below EMA9 | " +
                            $"price={liveBuyPrice:F5}, " +
                            $"ema9={ema9:F5}"
                        );

                        return false;
                    }


                    action =
                        TradeAction.EnterBuy;


                    ResetState(s);


                    LogFile.Write(
                        $"[{s}] ✅ BUY breakout entry | " +
                        $"normalBreak={liveBullBreak}, " +
                        $"escape={liveBullEscape}, " +
                        $"heldFor={heldFor:F1}s, " +
                        $"price={liveBuyPrice:F5}"
                    );


                    return true;
                }


                LogFile.Write(
                    $"[{s}] BUY breakout holding | " +
                    $"heldFor={heldFor:F1}/" +
                    $"{profile.HoldSeconds}s, " +
                    $"normalBreak={liveBullBreak}, " +
                    $"escape={liveBullEscape}"
                );


                return false;
            }


            // ==================================================
            // 20. SELL BREAKOUT
            // ==================================================

            if (liveBearBreak ||
                liveBearEscape)
            {
                double breakoutDistance =
                    liveBearBreak
                        ? (recentLow -
                           liveSellPrice) / atr
                        : (c1.low -
                           liveSellPrice) / atr;


                double maxMoveAtr =
                    IsGold(s) ? 1.60 :
                    IsCrypto(s) ? 2.20 :
                    IsIndex(s) ? 1.70 :
                    IsFx(s) ? 1.30 :
                    1.50;


                double moveFromRecentHighAtr =
                    (recentHigh -
                     liveSellPrice) / atr;


                double emaDist =
                    Math.Abs(
                        liveSellPrice -
                        ema9
                    ) / atr;


                bool trendContinuation =
                    Math.Abs(momentum) > 4 &&
                    bodyAtrCheck > 0.50 &&
                    emaDist > 0.15;


                bool lateImpulseContinuation =
                    bodyAtrCheck > 1.00 &&
                    Math.Abs(momentum) > 6;


                bool allowExtendedMove =
                    strongImpulse ||
                    trendContinuation ||
                    lateImpulseContinuation;


                if (moveFromRecentHighAtr >
                        maxMoveAtr &&
                    !allowExtendedMove)
                {
                    ResetState(s);

                    WaitingForPullback[s] =
                        true;

                    LogFile.Write(
                        $"[{s}] ❌ SELL blocked: extended move | " +
                        $"moveATR={moveFromRecentHighAtr:F2}, " +
                        $"max={maxMoveAtr:F2}"
                    );

                    return false;
                }


                if (distSellFromEma9Atr >
                    profile.MaxDistanceFromEma9Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] SELL blocked: too far from EMA9 | " +
                        $"dist={distSellFromEma9Atr:F2}, " +
                        $"max={profile.MaxDistanceFromEma9Atr:F2}"
                    );

                    return false;
                }


                if (distSellFromEma21Atr >
                    profile.MaxDistanceFromEma21Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] SELL blocked: too far from EMA21 | " +
                        $"dist={distSellFromEma21Atr:F2}, " +
                        $"max={profile.MaxDistanceFromEma21Atr:F2}"
                    );

                    return false;
                }


                if (breakoutDistance >
                    profile.MaxBreakoutDistanceAtr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] SELL blocked: chase breakout | " +
                        $"breakoutDistanceAtr={breakoutDistance:F2}, " +
                        $"max={profile.MaxBreakoutDistanceAtr:F2}"
                    );

                    return false;
                }


                double maxEarlyDistance =
                    IsGold(s) ? 0.50 :
                    IsCrypto(s) ? 0.80 :
                    IsIndex(s) ? 0.60 :
                    IsFx(s) ? 0.40 :
                    0.50;


                if (distSellFromEma9Atr >
                    maxEarlyDistance)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ❌ Early breakout blocked: too extended | " +
                        $"emaDist={distSellFromEma9Atr:F2}, " +
                        $"max={maxEarlyDistance:F2}"
                    );

                    return false;
                }


                // ==================================================
                // ULTRA MOMENTUM SELL
                // ==================================================

                bool ultraMomentum =
                    bodyAtrCheck > 0.90;


                if (liveBearEscape &&
                    ultraMomentum &&
                    !choppySetup)
                {
                    action =
                        TradeAction.EnterSell;


                    ResetState(s);


                    LogFile.Write(
                        $"[{s}] ⚡ ULTRA MOMENTUM SELL | " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"emaDist={distSellFromEma9Atr:F2}, " +
                        $"choppy={choppySetup}"
                    );


                    return true;
                }


                // ==================================================
                // QUALITY INSTANT SELL
                // ==================================================

                if (liveBearEscape &&
                    instantSellQuality)
                {
                    action =
                        TradeAction.EnterSell;


                    ResetState(s);


                    LogFile.Write(
                        $"[{s}] ⚡ INSTANT SELL (quality breakout) | " +
                        $"price={liveSellPrice:F5}, " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"emaDist={distSellFromEma9Atr:F2}, " +
                        $"choppy={choppySetup}"
                    );


                    return true;
                }


                if (liveBearEscape &&
                    !instantSellQuality)
                {
                    LogFile.Write(
                        $"[{s}] ❌ INSTANT SELL blocked: weak/choppy breakout | " +
                        $"bodyAtr={bodyAtrCheck:F2}/{profile.InstantMinBodyAtr:F2}, " +
                        $"emaDist={distSellFromEma9Atr:F2}/" +
                        $"{profile.InstantMinEmaDistanceAtr:F2}-" +
                        $"{profile.InstantMaxEmaDistanceAtr:F2}, " +
                        $"choppy={choppySetup}"
                    );
                }


                // ==================================================
                // NORMAL DELAYED SELL
                // ==================================================

                if (!state.BearBreakDetected)
                {
                    state.BearBreakDetected =
                        true;


                    state.BullBreakDetected =
                        false;


                    state.FirstBreakTimeUtc =
                        DateTime.UtcNow;


                    LogFile.Write(
                        $"[{s}] SELL breakout detected | " +
                        $"normalBreak={liveBearBreak}, " +
                        $"escape={liveBearEscape}, " +
                        $"hold={profile.HoldSeconds}s, " +
                        $"price={liveSellPrice:F5}, " +
                        $"recentLow={recentLow:F5}, " +
                        $"c1Low={c1.low:F5}, " +
                        $"emaSpreadAtr={emaSpreadAtr:F2}, " +
                        $"bodyAtr={bodyAtrCheck:F2}, " +
                        $"bodyRatio={bodyRatio:F2}, " +
                        $"breakoutDistanceAtr={breakoutDistance:F2}, " +
                        $"maxBreakoutDistanceAtr={profile.MaxBreakoutDistanceAtr:F2}"
                    );


                    return false;
                }


                double heldFor =
                    (DateTime.UtcNow -
                     state.FirstBreakTimeUtc)
                    .TotalSeconds;


                if (heldFor >=
                    profile.HoldSeconds)
                {
                    if (liveSellPrice > ema9)
                    {
                        ResetState(s);

                        LogFile.Write(
                            $"[{s}] SELL blocked after hold: " +
                            $"price moved above EMA9 | " +
                            $"price={liveSellPrice:F5}, " +
                            $"ema9={ema9:F5}"
                        );

                        return false;
                    }


                    action =
                        TradeAction.EnterSell;


                    ResetState(s);


                    LogFile.Write(
                        $"[{s}] ✅ SELL breakout entry | " +
                        $"normalBreak={liveBearBreak}, " +
                        $"escape={liveBearEscape}, " +
                        $"heldFor={heldFor:F1}s, " +
                        $"price={liveSellPrice:F5}"
                    );


                    return true;
                }


                LogFile.Write(
                    $"[{s}] SELL breakout holding | " +
                    $"heldFor={heldFor:F1}/" +
                    $"{profile.HoldSeconds}s, " +
                    $"normalBreak={liveBearBreak}, " +
                    $"escape={liveBearEscape}"
                );


                return false;
            }


            // ==================================================
            // 21. SELL PULLBACK RE-ENTRY
            // ==================================================

            if (WaitingForPullback.ContainsKey(s) &&
                WaitingForPullback[s])
            {
                double pullbackDist =
                    Math.Abs(
                        c0.close -
                        ema9
                    ) / atr;


                bool pulledBackToEma =
                    pullbackDist <
                    0.35;


                bool weakBullishBounce =
                    c0.close > c0.open &&
                    bodyRatio < 0.50;


                bool bearishContinuation =
                    c1.close < c1.open &&
                    c1.close < ema9;


                if (pulledBackToEma &&
                    weakBullishBounce &&
                    bearishContinuation)
                {
                    WaitingForPullback[s] =
                        false;


                    LogFile.Write(
                        $"[{s}] 🔁 SELL re-entry after pullback | " +
                        $"pullbackDist={pullbackDist:F2}"
                    );


                    action =
                        TradeAction.EnterSell;


                    return true;
                }
            }


            // ==================================================
            // 22. BUY PULLBACK RE-ENTRY
            // ==================================================

            if (WaitingForPullback.ContainsKey(s) &&
                WaitingForPullback[s])
            {
                double pullbackDist =
                    Math.Abs(
                        c0.close -
                        ema9
                    ) / atr;


                bool pulledBackToEma =
                    pullbackDist <
                    0.35;


                bool weakBearishPullback =
                    c0.close < c0.open &&
                    bodyRatio < 0.50;


                bool bullishContinuation =
                    c1.close > c1.open &&
                    c1.close > ema9;


                if (pulledBackToEma &&
                    weakBearishPullback &&
                    bullishContinuation)
                {
                    WaitingForPullback[s] =
                        false;


                    LogFile.Write(
                        $"[{s}] 🔁 BUY re-entry after pullback | " +
                        $"pullbackDist={pullbackDist:F2}"
                    );


                    action =
                        TradeAction.EnterBuy;


                    return true;
                }
            }


            // ==================================================
            // 23. NO ENTRY
            // ==================================================

            LogFile.Write(
                $"[{s}] Breakout no entry | " +
                $"trendUp={trendUp}, " +
                $"trendDown={trendDown}, " +
                $"normalBuy={liveBullBreak}, " +
                $"normalSell={liveBearBreak}, " +
                $"bullEscape={liveBullEscape}, " +
                $"bearEscape={liveBearEscape}, " +
                $"emaSpreadAtr={emaSpreadAtr:F2}, " +
                $"momentum={momentum:F2}, " +
                $"bodyAtr={bodyAtrCheck:F2}, " +
                $"choppy={choppySetup}, " +
                $"priceBuy={liveBuyPrice:F5}, " +
                $"priceSell={liveSellPrice:F5}, " +
                $"recentHigh={recentHigh:F5}, " +
                $"recentLow={recentLow:F5}"
            );


            return false;
        }


        // ==================================================
        // CHOP DETECTION FOR INSTANT BREAKOUTS
        // ==================================================

        private static bool IsChoppyForInstantEntry(
            string symbol,
            MqlRates[] rates,
            double atr,
            Profile profile)
        {
            if (rates == null ||
                rates.Length < 4 ||
                atr <= 0)
            {
                return false;
            }


            MqlRates b0 =
                rates[^1];


            MqlRates b1 =
                rates[^2];


            MqlRates b2 =
                rates[^3];


            double closeMove1 =
                Math.Abs(
                    b0.close -
                    b1.close
                ) / atr;


            double closeMove2 =
                Math.Abs(
                    b1.close -
                    b2.close
                ) / atr;


            double range0 =
                (b0.high -
                 b0.low) / atr;


            double range1 =
                (b1.high -
                 b1.low) / atr;


            bool closesCompressed =
                closeMove1 <
                    profile.ChoppyCloseDistanceAtr &&
                closeMove2 <
                    profile.ChoppyCloseDistanceAtr;


            bool rangesSmall =
                range0 <
                    profile.ChoppyRangeAtr &&
                range1 <
                    profile.ChoppyRangeAtr;


            // Index needs BOTH compression and small ranges.
            //
            // Other instruments are less aggressively filtered.

            if (IsIndex(symbol))
            {
                return
                    closesCompressed &&
                    rangesSmall;
            }


            return
                closesCompressed &&
                rangesSmall &&
                Math.Abs(b0.close - b2.close) <
                    atr * 0.30;
        }


        // ==================================================
        // SYMBOL-SENSITIVE PROFILE
        // ==================================================

        private static Profile GetProfile(
            string symbol,
            MarketState state)
        {
            symbol =
                NormalizeSymbol(symbol);


            // ==================================================
            // GOLD / METALS
            // ==================================================

            if (IsGold(symbol))
            {
                return new Profile
                {
                    StructureLookback = 5,

                    BreakBufferAtr = 0.12,

                    MaxDistanceFromEma9Atr = 1.80,

                    MaxDistanceFromEma21Atr = 2.80,

                    MaxBreakoutDistanceAtr = 0.70,

                    MinMomentum = 1.60,

                    MinEmaSpreadAtr = 0.20,

                    HoldSeconds = 14,


                    InstantMinBodyAtr = 0.65,

                    InstantMinEmaDistanceAtr = 0.10,

                    InstantMaxEmaDistanceAtr = 0.70,

                    ChoppyCloseDistanceAtr = 0.18,

                    ChoppyRangeAtr = 0.65
                };
            }


            // ==================================================
            // CRYPTO
            // ==================================================

            if (IsCrypto(symbol))
            {
                return new Profile
                {
                    StructureLookback = 5,

                    BreakBufferAtr = 0.10,

                    MaxDistanceFromEma9Atr = 2.60,

                    MaxDistanceFromEma21Atr = 3.50,

                    MaxBreakoutDistanceAtr = 0.85,

                    MinMomentum =
                        state == MarketState.EarlyBreakout
                            ? 1.125
                            : 1.50,

                    MinEmaSpreadAtr = 0.10,

                    HoldSeconds =
                        state == MarketState.EarlyBreakout
                            ? 4
                            : 6,


                    InstantMinBodyAtr = 0.70,

                    InstantMinEmaDistanceAtr = 0.12,

                    InstantMaxEmaDistanceAtr = 0.80,

                    ChoppyCloseDistanceAtr = 0.16,

                    ChoppyRangeAtr = 0.70
                };
            }


            // ==================================================
            // INDICES
            // ==================================================

            if (IsIndex(symbol))
            {
                return new Profile
                {
                    StructureLookback = 5,

                    BreakBufferAtr = 0.10,

                    MaxDistanceFromEma9Atr = 1.60,

                    MaxDistanceFromEma21Atr = 2.50,

                    MaxBreakoutDistanceAtr = 0.60,

                    MinMomentum =
                        state == MarketState.EarlyBreakout
                            ? 1.20
                            : 1.40,

                    MinEmaSpreadAtr = 0.12,

                    HoldSeconds =
                        state == MarketState.EarlyBreakout
                            ? 5
                            : 8,


                    // IMPORTANT:
                    // US100 instant breakouts need more conviction.

                    InstantMinBodyAtr = 0.70,

                    InstantMinEmaDistanceAtr = 0.12,

                    InstantMaxEmaDistanceAtr = 0.60,

                    ChoppyCloseDistanceAtr = 0.20,

                    ChoppyRangeAtr = 0.70
                };
            }


            // ==================================================
            // FX
            // ==================================================

            if (IsFx(symbol))
            {
                return new Profile
                {
                    StructureLookback = 5,

                    BreakBufferAtr = 0.08,

                    MaxDistanceFromEma9Atr = 1.60,

                    MaxDistanceFromEma21Atr = 2.50,

                    MaxBreakoutDistanceAtr = 0.55,

                    MinMomentum = 1.10,

                    MinEmaSpreadAtr = 0.08,

                    HoldSeconds = 12,


                    InstantMinBodyAtr = 0.55,

                    InstantMinEmaDistanceAtr = 0.08,

                    InstantMaxEmaDistanceAtr = 0.55,

                    ChoppyCloseDistanceAtr = 0.18,

                    ChoppyRangeAtr = 0.65
                };
            }


            // ==================================================
            // DEFAULT
            // ==================================================

            return new Profile
            {
                StructureLookback = 5,

                BreakBufferAtr = 0.08,

                MaxDistanceFromEma9Atr = 1.90,

                MaxDistanceFromEma21Atr = 2.80,

                MaxBreakoutDistanceAtr = 0.75,

                MinMomentum = 1.10,

                MinEmaSpreadAtr = 0.10,

                HoldSeconds = 15,


                InstantMinBodyAtr = 0.65,

                InstantMinEmaDistanceAtr = 0.10,

                InstantMaxEmaDistanceAtr = 0.70,

                ChoppyCloseDistanceAtr = 0.18,

                ChoppyRangeAtr = 0.70
            };
        }


        // ==================================================
        // RESET
        // ==================================================

        private static void ResetState(
            string symbol)
        {
            symbol =
                NormalizeSymbol(symbol);


            if (!_states.ContainsKey(symbol))
            {
                _states[symbol] =
                    new LiveBreakState();
            }


            _states[symbol]
                .BullBreakDetected =
                false;


            _states[symbol]
                .BearBreakDetected =
                false;


            _states[symbol]
                .FirstBreakTimeUtc =
                DateTime.MinValue;
        }


        // ==================================================
        // SYMBOL NORMALIZATION
        // ==================================================

        private static string NormalizeSymbol(
            string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return string.Empty;


            return symbol
                .Trim()
                .ToUpperInvariant()
                .Replace(".S", "")
                .Replace("_I", "")
                .Replace(".CASH", "");
        }


        // ==================================================
        // GOLD
        // ==================================================

        private static bool IsGold(
            string symbol)
        {
            symbol =
                NormalizeSymbol(symbol);


            return
                symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER");
        }


        // ==================================================
        // CRYPTO
        // ==================================================

        private static bool IsCrypto(
            string symbol)
        {
            symbol =
                NormalizeSymbol(symbol);


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


        // ==================================================
        // INDICES
        // ==================================================

        private static bool IsIndex(
            string symbol)
        {
            symbol =
                NormalizeSymbol(symbol);


            return
                symbol.Contains("US100") ||
                symbol.Contains("NAS") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US500") ||
                symbol.Contains("SPX") ||
                symbol.Contains("SP500") ||
                symbol.Contains("US30") ||
                symbol.Contains("DJI") ||
                symbol.Contains("GER40") ||
                symbol.Contains("UK100") ||
                symbol.Contains("JP225");
        }


        // ==================================================
        // FX
        //
        // IMPORTANT:
        // BTCUSD, ETHUSD, BNBUSD etc. are NOT FX.
        // ==================================================

        private static bool IsFx(
            string symbol)
        {
            symbol =
                NormalizeSymbol(symbol);


            // Reject known non-FX instruments first.

            if (IsGold(symbol) ||
                IsCrypto(symbol) ||
                IsIndex(symbol))
            {
                return false;
            }


            if (symbol.Length < 6)
                return false;


            string pair =
                symbol.Substring(0, 6);


            string baseCcy =
                pair.Substring(0, 3);


            string quoteCcy =
                pair.Substring(3, 3);


            string[] currencies =
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


            bool validBase =
                currencies.Contains(baseCcy);


            bool validQuote =
                currencies.Contains(quoteCcy);


            return
                validBase &&
                validQuote &&
                baseCcy != quoteCcy;
        }
    }
}
