using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public static class BreakoutContinuationEngine
    {
        private class Profile
        {
            public int StructureLookback { get; set; }
            public double BreakBufferAtr { get; set; }
            public double MaxDistanceFromEma9Atr { get; set; }
            public double MaxDistanceFromEma21Atr { get; set; }

            // NEW: controls how far price can break beyond structure
            // before it is considered a chase entry.
            public double MaxBreakoutDistanceAtr { get; set; }

            public double MinMomentum { get; set; }
            public double MinEmaSpreadAtr { get; set; }
            public int HoldSeconds { get; set; }
            
        }

        private class LiveBreakState
        {
            public bool BullBreakDetected;
            public bool BearBreakDetected;
            public DateTime FirstBreakTimeUtc;
        }

        private static readonly Dictionary<string, LiveBreakState> _states = new();
        private static readonly Dictionary<string, bool> WaitingForPullback = new();
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

            string s = NormalizeSymbol(symbol);
            var profile = GetProfile(s, marketState);

            if (!_states.ContainsKey(s))
                _states[s] = new LiveBreakState();

            var state = _states[s];

            var c0 = rates[^1]; // latest closed candle
            var c1 = rates[^2]; // previous closed candle

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            bool trendUp = ema9 > ema21 && emaFast[^1] > emaFast[^2];
            bool trendDown = ema9 < ema21 && emaFast[^1] < emaFast[^2];

            double emaSpreadAtr = Math.Abs(ema9 - ema21) / atr;
            bool emaSpreadTooSmall = emaSpreadAtr < profile.MinEmaSpreadAtr;

            double bodyAtrCheck = Math.Abs(c0.close - c0.open) / atr;

            bool strongImpulse = bodyAtrCheck >= 0.7;
            bool mediumImpulse = bodyAtrCheck >= 0.4;

            // 🔥 Allow strong breakout candles to bypass momentum filter (GOLD ONLY)
            bool allowMomentumBypass =
                IsGold(symbol) &&
                bodyAtrCheck > 1.1;   // 🔥 captures your 1.19 case

            bool allowWeakMomentumForEarly =
             mediumImpulse && bodyAtrCheck < 0.7; // pre-breakout zone

            if (!allowWeakMomentumForEarly &&
                Math.Abs(momentum) < profile.MinMomentum &&
                !allowMomentumBypass)
            {
                ResetState(s);

                LogFile.Write(
                    $"[{s}] Breakout blocked: weak momentum | " +
                    $"momentum={momentum:F2}, min={profile.MinMomentum:F2}, bodyAtr={bodyAtrCheck:F2}"
                );

                return false;
            }

            int lookback = Math.Min(profile.StructureLookback, rates.Length - 2);

            double recentHigh = double.MinValue;
            double recentLow = double.MaxValue;

            for (int i = 2; i <= lookback + 1; i++)
            {
                recentHigh = Math.Max(recentHigh, rates[^i].high);
                recentLow = Math.Min(recentLow, rates[^i].low);
            }

            double buffer = atr * profile.BreakBufferAtr;

            double liveBuyPrice = ask;
            double liveSellPrice = bid;

            double body = Math.Abs(c0.close - c0.open);
            double range = c0.high - c0.low;
            double bodyRatio = range > 0 ? body / range : 0;
            double bodyAtr = Math.Abs(c0.close - c0.open) / atr;

            bool strongBullBody =
                c0.close > c0.open &&
                body >= atr * 0.35 &&
                bodyRatio >= 0.45 &&
                bodyAtr > 0.6;   // 🔥 NEW

            
            bool strongBearBody =
                c0.close < c0.open &&
                body >= atr * 0.35 &&
                bodyRatio >= 0.45 &&
                bodyAtr > 0.6;   // 🔥 NEW FILTER (CRITICAL)

            // ============================
            // NORMAL STRUCTURE BREAKOUT
            // ============================
            bool liveBullBreak =
                trendUp &&
                liveBuyPrice > recentHigh + buffer;

            bool liveBearBreak =
                trendDown &&
                liveSellPrice < recentLow - buffer;

            // ============================
            // EARLY ESCAPE BREAKOUT
            // This matches MarketStateEngine bullEscape / bearEscape.
            // It allows entry before EMA spread fully opens.
            // ============================
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

            // If EMA spread is too small, block normal continuation,
            // but still allow true escape breakout.
            if (emaSpreadTooSmall && !liveBullEscape && !liveBearEscape)
            {
                ResetState(s);

                LogFile.Write(
                    $"[{s}] Breakout blocked: EMA spread too small and no escape | " +
                    $"emaSpreadAtr={emaSpreadAtr:F2}, min={profile.MinEmaSpreadAtr:F2}, " +
                    $"bullEscape={liveBullEscape}, bearEscape={liveBearEscape}"
                );

                return false;
            }

            // If there is no mature trend, still allow escape breakout.
            // But do not allow normal structure breakout without trend.
            if (!trendUp && !trendDown && !liveBullEscape && !liveBearEscape)
            {
                ResetState(s);

                LogFile.Write(
                    $"[{s}] Breakout blocked: no trend and no escape | " +
                    $"trendUp={trendUp}, trendDown={trendDown}, " +
                    $"bullEscape={liveBullEscape}, bearEscape={liveBearEscape}"
                );

                return false;
            }

            double distBuyFromEma9Atr = Math.Abs(liveBuyPrice - ema9) / atr;
            double distSellFromEma9Atr = Math.Abs(liveSellPrice - ema9) / atr;

            double distBuyFromEma21Atr = Math.Abs(liveBuyPrice - ema21) / atr;
            double distSellFromEma21Atr = Math.Abs(liveSellPrice - ema21) / atr;


            // ============================
            // 🔥 PRE-BREAKOUT ENTRY (NEW)
            // ============================
            double maxPreBreakoutDist =
                IsCrypto(symbol) ? 0.4 :   // tighter for crypto
                IsIndex(symbol) ? 0.6 :
                IsGold(symbol) ? 0.5 :
                0.5;

            double maxEmaDist =
                IsCrypto(symbol) ? 0.6 :   // tighter
                IsIndex(symbol) ? 0.8 :
                IsGold(symbol) ? 0.7 :
                0.7;

            double minBodyAtr =
                IsCrypto(symbol) ? 0.7 :   // stronger momentum required
                IsIndex(symbol) ? 0.5 :
                IsGold(symbol) ? 0.6 :
                0.5;

            double distToBreakoutAtr = (recentHigh - liveBuyPrice) / atr;
            double distToBreakdownAtr = (liveSellPrice - recentLow) / atr;


            double minMomentum =
                IsCrypto(symbol) ? 4.0 :
                IsIndex(symbol) ? 5.0 :
                IsGold(symbol) ? 3.5 :
                3.0;

            bool strongMomentum = momentum >= minMomentum;
            // 🔵 BUY
            bool preBreakoutBuy =
                trendUp &&
                liveBuyPrice > ema9 &&
                liveBuyPrice > ema21 &&
                distToBreakoutAtr > 0 &&
                distToBreakoutAtr < maxPreBreakoutDist &&
                bodyAtrCheck > minBodyAtr &&
                distBuyFromEma9Atr < maxEmaDist &&
                strongMomentum; // 🔥 NEW;;

            if (preBreakoutBuy)
            {
                bool allowPreBreakout =
                strongMomentum &&
                bodyAtrCheck > 1.0;

                if (!allowPreBreakout)
                {
                    LogFile.Write("❌ Blocked: weak pre-breakout BUY conditions");
                    return false;
                }
                action = TradeAction.EnterBuy;

                LogFile.Write(
                    $"[{symbol}] ⚡ PRE-BREAKOUT BUY | dist={distToBreakoutAtr:F2}, bodyAtr={bodyAtrCheck:F2}"
                );

                return true;
            }

            // 🔴 SELL
            bool preBreakoutSell =
                trendDown &&
                liveSellPrice < ema9 &&
                liveSellPrice < ema21 &&
                distToBreakdownAtr > 0 &&
                distToBreakdownAtr < maxPreBreakoutDist &&
                bodyAtrCheck > minBodyAtr &&
                distSellFromEma9Atr < maxEmaDist &&
                strongMomentum; // 🔥 NEW;;

            if (preBreakoutSell)
            {
                bool allowPreBreakout =
                strongMomentum &&
                bodyAtrCheck > 1.0;

                if (!allowPreBreakout)
                {
                    LogFile.Write("❌ Blocked: weak pre-breakout SELL conditions");
                    return false;
                }
                action = TradeAction.EnterSell;

                LogFile.Write(
                    $"[{symbol}] ⚡ PRE-BREAKOUT SELL | dist={distToBreakdownAtr:F2}, bodyAtr={bodyAtrCheck:F2}"
                );

                return true;
            }
            // ============================
            // BUY LOGIC
            // ============================
            if (liveBullBreak || liveBullEscape)
            {
                double breakoutDistance = liveBullBreak
                    ? (liveBuyPrice - recentHigh) / atr
                    : (liveBuyPrice - c1.high) / atr;

                // 🔥 DYNAMIC MAX MOVE PER ASSET
                double maxMoveAtr =
                IsGold(symbol) ? 1.5 :   // was 1.6
                IsFx(symbol) ? 1.2 :
                IsIndex(symbol) ? 1.5 :  // was 1.7
                IsCrypto(symbol) ? 2.0 :
                1.4;

                // 🔥 BLOCK LATE MOVE
                double moveFromRecentLowAtr = (liveBuyPrice - recentLow) / atr;

                double emaDist = Math.Abs(liveBuyPrice - ema9) / atr;

                bool trendContinuation =
                    Math.Abs(momentum) > 4 &&
                    bodyAtrCheck > 0.5 &&
                    emaDist > 0.15;

                bool lateImpulseContinuation =
                    bodyAtrCheck > 1.0 &&
                    Math.Abs(momentum) > 6;

                bool allowExtendedMove =
                    strongImpulse ||
                    trendContinuation ||
                    lateImpulseContinuation;

                if (moveFromRecentLowAtr > maxMoveAtr && !allowExtendedMove)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ❌ BUY blocked: extended move | moveATR={moveFromRecentLowAtr:F2}, max={maxMoveAtr:F2}"
                    );

                    WaitingForPullback[symbol] = true;

                    return false;
                }

                // Block late entries from EMA9
                if (distBuyFromEma9Atr > profile.MaxDistanceFromEma9Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] BUY blocked: too far from EMA9 | " +
                        $"dist={distBuyFromEma9Atr:F2}, max={profile.MaxDistanceFromEma9Atr:F2}"
                    );

                    return false;
                }

                // Block late entries from EMA21
                if (distBuyFromEma21Atr > profile.MaxDistanceFromEma21Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] BUY blocked: too far from EMA21 | " +
                        $"dist={distBuyFromEma21Atr:F2}, max={profile.MaxDistanceFromEma21Atr:F2}"
                    );

                    return false;
                }

                // Block chase entries
                if (breakoutDistance > profile.MaxBreakoutDistanceAtr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] BUY blocked: chase breakout | " +
                        $"breakoutDistanceAtr={breakoutDistance:F2}, " +
                        $"max={profile.MaxBreakoutDistanceAtr:F2}"
                    );

                    return false;
                }

                // 🔥 BLOCK LATE EARLY BREAKOUT (CRITICAL FIX)
                double maxEarlyDistance =
                    IsGold(symbol) ? 0.7 :
                    IsFx(symbol) ? 0.4 :
                    IsIndex(symbol) ? 0.6 :
                    IsCrypto(symbol) ? 0.8 :
                    0.5;

                if (distBuyFromEma9Atr > maxEarlyDistance)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ❌ Early breakout blocked: too extended | " +
                        $"emaDist={distBuyFromEma9Atr:F2}, max={maxEarlyDistance:F2}"
                    );

                    return false;
                }

                bool ultraMomentum =
                  (Math.Abs(c0.close - c0.open) / atr) > 0.9;

                if (liveBullEscape && ultraMomentum)
                {
                    action = TradeAction.EnterBuy;

                    LogFile.Write($"[{symbol}] ⚡ ULTRA MOMENTUM BUY");

                    return true;
                }

                // 🔥 INSTANT ENTRY FOR EARLY BREAKOUT
                if (liveBullEscape)
                {
                    action = TradeAction.EnterBuy;
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ⚡ INSTANT BUY (early breakout) | price={liveBuyPrice:F5}"
                    );

                    return true;
                }

                // normal delayed breakout
                if (!state.BullBreakDetected)
                {
                    state.BullBreakDetected = true;
                    state.BearBreakDetected = false;
                    state.FirstBreakTimeUtc = DateTime.UtcNow;

                    LogFile.Write(
                        $"[{s}] BUY breakout detected | " +
                        $"normalBreak={liveBullBreak}, escape={liveBullEscape}, " +
                        $"hold={profile.HoldSeconds}s, price={liveBuyPrice:F5}, " +
                        $"recentHigh={recentHigh:F5}, c1High={c1.high:F5}, " +
                        $"emaSpreadAtr={emaSpreadAtr:F2}, bodyAtr={(body / atr):F2}, bodyRatio={bodyRatio:F2}, " +
                        $"breakoutDistanceAtr={breakoutDistance:F2}, maxBreakoutDistanceAtr={profile.MaxBreakoutDistanceAtr:F2}"
                    );

                    return false;
                }

                double heldFor =
                    (DateTime.UtcNow - state.FirstBreakTimeUtc).TotalSeconds;

                if (heldFor >= profile.HoldSeconds)
                {
                    // Ensure continuation
                    if (liveBuyPrice < ema9)
                    {
                        ResetState(s);

                        LogFile.Write(
                            $"[{s}] BUY blocked after hold: price fell below EMA9 | " +
                            $"price={liveBuyPrice:F5}, ema9={ema9:F5}"
                        );

                        return false;
                    }

                    action = TradeAction.EnterBuy;
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ✅ BUY breakout entry | " +
                        $"normalBreak={liveBullBreak}, escape={liveBullEscape}, " +
                        $"heldFor={heldFor:F1}s, price={liveBuyPrice:F5}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{s}] BUY breakout holding | " +
                    $"heldFor={heldFor:F1}/{profile.HoldSeconds}s, " +
                    $"normalBreak={liveBullBreak}, escape={liveBullEscape}"
                );

                return false;
            }

            // ============================
            // SELL LOGIC
            // ============================
            if (liveBearBreak || liveBearEscape)
            {
                double breakoutDistance = liveBearBreak
                    ? (recentLow - liveSellPrice) / atr
                    : (c1.low - liveSellPrice) / atr;

                // 🔥 DYNAMIC MAX MOVE PER ASSET
                double maxMoveAtr =
                    IsGold(symbol) ? 1.6 :
                    IsFx(symbol) ? 1.3 :
                    IsIndex(symbol) ? 1.7 :
                    IsCrypto(symbol) ? 2.2 :
                    1.5;

                // 🔥 BLOCK LATE MOVE
                double moveFromRecentHighAtr = (recentHigh - liveSellPrice) / atr;

                double emaDist = Math.Abs(liveSellPrice - ema9) / atr;

                bool trendContinuation =
                    Math.Abs(momentum) > 4 &&
                    bodyAtrCheck > 0.5 &&
                    emaDist > 0.15;

                bool lateImpulseContinuation =
                    bodyAtrCheck > 1.0 &&
                    Math.Abs(momentum) > 6;

                // 🔥 USE EXISTING strongImpulse (DO NOT REDECLARE)
                bool allowExtendedMove =
                    strongImpulse ||            // already defined above
                    trendContinuation ||
                    lateImpulseContinuation;

                if (moveFromRecentHighAtr > maxMoveAtr && !allowExtendedMove)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ❌ SELL blocked: extended move | moveATR={moveFromRecentHighAtr:F2}, max={maxMoveAtr:F2}"
                    );

                    WaitingForPullback[symbol] = true;

                    return false;
                }

                // Block late entries from EMA9
                if (distSellFromEma9Atr > profile.MaxDistanceFromEma9Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] SELL blocked: too far from EMA9 | " +
                        $"dist={distSellFromEma9Atr:F2}, max={profile.MaxDistanceFromEma9Atr:F2}"
                    );

                    return false;
                }

                // Block late entries from EMA21
                if (distSellFromEma21Atr > profile.MaxDistanceFromEma21Atr)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] SELL blocked: too far from EMA21 | " +
                        $"dist={distSellFromEma21Atr:F2}, max={profile.MaxDistanceFromEma21Atr:F2}"
                    );

                    return false;
                }

                // Block chase entries
                if (breakoutDistance > profile.MaxBreakoutDistanceAtr)
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
                    IsGold(symbol) ? 0.5 :
                    IsFx(symbol) ? 0.4 :
                    IsIndex(symbol) ? 0.6 :
                    IsCrypto(symbol) ? 0.8 :
                    0.5;

                if (distSellFromEma9Atr > maxEarlyDistance)
                {
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ❌ Early breakout blocked: too extended | " +
                        $"emaDist={distSellFromEma9Atr:F2}, max={maxEarlyDistance:F2}"
                    );

                    return false;
                }

                bool ultraMomentum =
                  (Math.Abs(c0.close - c0.open) / atr) > 0.9;

                if (liveBearEscape && ultraMomentum)
                {
                    action = TradeAction.EnterSell;

                    LogFile.Write($"[{symbol}] ⚡ ULTRA MOMENTUM SELL");

                    return true;
                }

                // normal delayed breakout
                if (!state.BearBreakDetected)
                {
                    state.BearBreakDetected = true;
                    state.BullBreakDetected  = false;
                    state.FirstBreakTimeUtc = DateTime.UtcNow;

                    LogFile.Write(
                        $"[{s}] SELL breakout detected | " +
                        $"normalBreak={liveBearBreak}, escape={liveBearEscape}, " +
                        $"hold={profile.HoldSeconds}s, price={liveSellPrice:F5}, " +
                        $"recentLow={recentLow:F5}, c1Low={c1.low:F5}, " +
                        $"emaSpreadAtr={emaSpreadAtr:F2}, bodyAtr={(body / atr):F2}, bodyRatio={bodyRatio:F2}, " +
                        $"breakoutDistanceAtr={breakoutDistance:F2}, maxBreakoutDistanceAtr={profile.MaxBreakoutDistanceAtr:F2}"
                    );

                    return false;
                }

                double heldFor =
                    (DateTime.UtcNow - state.FirstBreakTimeUtc).TotalSeconds;

                if (heldFor >= profile.HoldSeconds)
                {
                    // Ensure continuation
                    if (liveSellPrice > ema9)
                    {
                        ResetState(s);

                        LogFile.Write(
                            $"[{s}] SELL blocked after hold: price moved above EMA9 | " +
                            $"price={liveSellPrice:F5}, ema9={ema9:F5}"
                        );

                        return false;
                    }

                    action = TradeAction.EnterSell;
                    ResetState(s);

                    LogFile.Write(
                        $"[{s}] ✅ SELL breakout entry | " +
                        $"normalBreak={liveBearBreak}, escape={liveBearEscape}, " +
                        $"heldFor={heldFor:F1}s, price={liveSellPrice:F5}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{s}] SELL breakout holding | " +
                    $"heldFor={heldFor:F1}/{profile.HoldSeconds}s, " +
                    $"normalBreak={liveBearBreak}, escape={liveBearEscape}"
                );

                return false;
            }

            // ============================
            // 🔁 PULLBACK RE-ENTRY (SELL)
            // ============================
            if (WaitingForPullback.ContainsKey(symbol) && WaitingForPullback[symbol])
            {
                double pullbackDist = Math.Abs(c0.close - ema9) / atr;

                bool pulledBackToEma =
                    pullbackDist < 0.35;

                bool weakBullishBounce =
                    c0.close > c0.open &&
                    bodyRatio < 0.5;

                bool bearishContinuation =
                    c1.close < c1.open &&
                    c1.close < ema9;

                if (pulledBackToEma && weakBullishBounce && bearishContinuation)
                {
                    WaitingForPullback[symbol] = false;

                    LogFile.Write(
                        $"[{s}] 🔁 SELL re-entry after pullback | " +
                        $"pullbackDist={pullbackDist:F2}"
                    );

                    action = TradeAction.EnterSell;
                    return true;
                }
            }

            // ============================
            // 🔁 PULLBACK RE-ENTRY (BUY)
            // ============================
            if (WaitingForPullback.ContainsKey(symbol) && WaitingForPullback[symbol])
            {
                double pullbackDist = Math.Abs(c0.close - ema9) / atr;

                bool pulledBackToEma =
                    pullbackDist < 0.35;

                bool weakBearishPullback =
                    c0.close < c0.open &&
                    bodyRatio < 0.5;

                bool bullishContinuation =
                    c1.close > c1.open &&
                    c1.close > ema9;

                if (pulledBackToEma && weakBearishPullback && bullishContinuation)
                {
                    WaitingForPullback[symbol] = false;

                    LogFile.Write(
                        $"[{symbol}] 🔁 BUY re-entry after pullback | " +
                        $"pullbackDist={pullbackDist:F2}"
                    );

                    action = TradeAction.EnterBuy;
                    return true;
                }
            }
            LogFile.Write(
                $"[{symbol}] Breakout no entry | " +
                $"trendUp={trendUp}, trendDown={trendDown}, " +
                $"normalBuy={liveBullBreak}, normalSell={liveBearBreak}, " +
                $"bullEscape={liveBullEscape}, bearEscape={liveBearEscape}, " +
                $"emaSpreadAtr={emaSpreadAtr:F2}, momentum={momentum:F2}, " +
                $"priceBuy={liveBuyPrice:F5}, priceSell={liveSellPrice:F5}, " +
                $"recentHigh={recentHigh:F5}, recentLow={recentLow:F5}, " +
                $"c1High={c1.high:F5}, c1Low={c1.low:F5}"
            );

            return false;
        }

        private static Profile GetProfile(string symbol, MarketState state)
        {
            if (IsGold(symbol))
            {
                return new Profile
                {
                    StructureLookback = 5,
                    BreakBufferAtr = 0.12,

                    // 🔥 tighten this
                    MaxDistanceFromEma9Atr = 1.80,   // was 2.30

                    // slightly tighten
                    MaxDistanceFromEma21Atr = 2.80,  // was 3.20

                    MaxBreakoutDistanceAtr = 0.70,

                    // keep strong filter
                    MinMomentum = 1.60,

                    // good
                    MinEmaSpreadAtr = 0.20,

                    // 🔥 slightly faster reaction
                    HoldSeconds = 14   // was 18
                };
            }

            if (IsCrypto(symbol))
            {
                return new Profile
                {
                    StructureLookback = 5,
                    BreakBufferAtr = 0.10,
                    MaxDistanceFromEma9Atr = 2.60,
                    MaxDistanceFromEma21Atr = 3.50,
                    MaxBreakoutDistanceAtr = 0.85,
                    MinMomentum = state == MarketState.EarlyBreakout ? 1.50 * 0.75 : 1.50,
                    MinEmaSpreadAtr = 0.10,
                    HoldSeconds = state == MarketState.EarlyBreakout ? 4 : 6
                };
            }

            return new Profile
            {
                StructureLookback = 5,
                BreakBufferAtr = 0.08,
                MaxDistanceFromEma9Atr = 1.90,
                MaxDistanceFromEma21Atr = 2.80,
                MaxBreakoutDistanceAtr = 0.75,
                MinMomentum = 1.10,
                MinEmaSpreadAtr = 0.10,
                HoldSeconds = 15
            };
        }

        private static void ResetState(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (!_states.ContainsKey(symbol))
                _states[symbol] = new LiveBreakState();

            _states[symbol].BullBreakDetected = false;
            _states[symbol].BearBreakDetected = false;
            _states[symbol].FirstBreakTimeUtc = DateTime.MinValue;
        }

        private static string NormalizeSymbol(string symbol)
        {
            return string.IsNullOrWhiteSpace(symbol)
                ? ""
                : symbol.Replace(".s", "")
                        .Replace("_i", "")
                        .Replace("m", "")
                        .ToUpperInvariant();
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") || symbol.Contains("XAG");
        }
        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") || symbol.Contains("NAS");
        }
        private static bool IsFx(string symbol)
        {
            return symbol.Contains("EUR") ||
                   symbol.Contains("USD") ||
                   symbol.Contains("JPY") ||
                   symbol.Contains("GBP") ||
                   symbol.Contains("AUD") ||
                   symbol.Contains("CAD") ||
                   symbol.Contains("CHF") ||
                   symbol.Contains("NZD");
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
    }
}
