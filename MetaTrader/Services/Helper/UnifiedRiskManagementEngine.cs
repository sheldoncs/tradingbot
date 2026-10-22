namespace MetaTrader.Services.Helper
{
    using global::MetaTrader.Data.Entities;
    using global::MetaTrader.Util;
    using MtApi5;
    using System;
    using static global::MetaTrader.Services.TradeServiceEngine;

    namespace MetaTrader.Services.Helper
    {
        using global::MetaTrader.Data.Entities;
        using global::MetaTrader.Util;
        using MtApi5;
        using System;
        using static global::MetaTrader.Services.TradeServiceEngine;

        public static class UnifiedRiskManagementEngine
        {
            // ============================================================
            // PUBLIC DECISION TYPES
            // ============================================================

            public enum RiskAction
            {
                Hold,
                Close,
                MoveStopLoss
            }

            public enum RiskStage
            {
                Initial,
                Developing,
                Protected,
                Trailing
            }

            public class RiskDecision
            {
                public RiskAction Action { get; set; }

                public RiskStage Stage { get; set; }

                public string Reason { get; set; }

                public double NewStopLoss { get; set; }


                public static RiskDecision Hold(
                    RiskStage stage,
                    string reason)
                {
                    return new RiskDecision
                    {
                        Action = RiskAction.Hold,
                        Stage = stage,
                        Reason = reason,
                        NewStopLoss = 0
                    };
                }


                public static RiskDecision Close(
                    RiskStage stage,
                    string reason)
                {
                    return new RiskDecision
                    {
                        Action = RiskAction.Close,
                        Stage = stage,
                        Reason = reason,
                        NewStopLoss = 0
                    };
                }


                public static RiskDecision MoveSL(
                    RiskStage stage,
                    double newSL,
                    string reason)
                {
                    return new RiskDecision
                    {
                        Action = RiskAction.MoveStopLoss,
                        Stage = stage,
                        Reason = reason,
                        NewStopLoss = newSL
                    };
                }
            }


            // ============================================================
            // SYMBOL PROFILE
            // ============================================================

            private class Profile
            {
                // Absolute minimum dollar loss per trade.
                // For most asset classes this remains the actual hard-loss threshold.
                public double HardLossUsd;

                // Optional volatility-aware hard-loss floor.
                //
                // When > 0, the engine converts ATR * HardLossAtrFloor into USD
                // using the symbol tick size/value and the actual trade volume.
                //
                // The effective hard-loss threshold becomes:
                //
                // max(HardLossUsd, ATR-based USD loss)
                //
                // and is then capped by MaxHardLossUsd when that value is > 0.
                //
                // This is currently enabled only for Gold.
                public double HardLossAtrFloor;

                // Optional absolute ceiling for the volatility-aware hard loss.
                // 0 = no additional ceiling.
                public double MaxHardLossUsd;

                // Structural failure protection.
                public double StructuralAdverseAtr;

                public double Ema21FailureAtr;

                public double OppositeBodyRatio;


                // ========================================================
                // PROFIT PROTECTION
                // ========================================================

                // Once peak reaches this amount, dollar-based
                // profit protection becomes active.
                public double ProtectAtProfitUsd;

                // Small ATR profit lock used during Protected stage.
                public double ProfitLockAtr;


                // ========================================================
                // RUNNER / TRAILING
                // ========================================================

                // Once peak reaches this level, ATR trailing begins.
                //
                // IMPORTANT:
                //
                // Dollar-based peak protection DOES NOT wait for this
                // level anymore.
                public double TrailAtProfitUsd;

                public double TrailDistanceAtr;


                // ========================================================
                // PEAK PROFIT PROTECTION
                //
                // These now become active once ProtectAtProfitUsd
                // has been reached.
                //
                // Example:
                //
                // Peak = $17.89
                //
                // 60% lock:
                // 17.89 * 0.60 = $10.73
                //
                // Max $7 giveback:
                // 17.89 - 7 = $10.89
                //
                // Strongest floor = $10.89
                // ========================================================

                public double PeakProfitLockPercent;

                public double MaxGivebackUsd;


                // Failed follow-through protection.
                public int FollowThroughBars;

                public double FollowThroughMinPeakProfitUsd;

                public double FollowThroughEma9FailureAtr;


                // ========================================================
                // GOLD EARLY TRANSITION FAILURE
                //
                // Optional Gold-only protection for a BreakoutContinuation
                // trade that fails very early after entry. Zero values disable
                // the rule for all other profiles.
                // ========================================================
                public int GoldEarlyFailureMaxBars;
                public double GoldEarlyFailureMaxPeakProfitUsd;
                public double GoldEarlyFailureAdverseAtr;
                public double GoldEarlyFailureEma9FailureAtr;


                // Minimum SL distance from current market.
                public double MinStopDistanceAtr;

                public double EarlyProtectAtProfitUsd;
                public double EarlyLockPercent;

                // Extra breathing room for the EARLY broker-side stop.
                // 0 means fall back to MinStopDistanceAtr.
                public double EarlyBrokerMinDistanceAtr;

                // If false, the early dollar floor will NOT force a software close.
                // Protection is handled by the wider broker-side stop instead.
                public bool EnableEarlySoftwareClose = true;


                // ========================================================
                // GOLD EARLY WINNER SAFETY FLOOR
                //
                // Gold needs more breathing room than other assets, but once
                // a trade has demonstrated meaningful profit it should not
                // remain protected only by the original losing SL.
                // ========================================================
                public double GoldEarlyWinnerArmUsd;
                public double GoldEarlyWinnerLockPercent;
                public double GoldEarlyWinnerMinLockUsd;
                public double GoldEarlyWinnerMaxLockUsd;
            }


            // ============================================================
            // MAIN RISK EVALUATION
            // ============================================================

            public static RiskDecision Evaluate(
                TrackedTrade trade,
                string symbol,
                double bid,
                double ask,
                double atr,
                MqlRates[] rates,
                double[] emaFast,
                double[] emaSlow,
                double brokerMinStopDistancePrice = 0,
                double tickSize = 0,
                double tickValue = 0)
            {
                if (trade == null)
                {
                    return RiskDecision.Hold(
                        RiskStage.Initial,
                        "Trade is null"
                    );
                }


                if (atr <= 0 ||
                    rates == null ||
                    rates.Length < 3 ||
                    emaFast == null ||
                    emaFast.Length < 2 ||
                    emaSlow == null ||
                    emaSlow.Length < 2)
                {
                    return RiskDecision.Hold(
                        RiskStage.Initial,
                        "Insufficient market data"
                    );
                }


                string s =
                    symbol?.ToUpperInvariant() ?? "";


                Profile profile =
                    GetProfile(s);


                // ========================================================
                // 1. UPDATE PEAK PROFIT
                //
                // IMPORTANT:
                //
                // MaxProfitUsd MUST remain persistent on TrackedTrade.
                // It must NOT be recreated every evaluation.
                // ========================================================

                trade.MaxProfitUsd =
                    Math.Max(
                        trade.MaxProfitUsd,
                        trade.ProfitUsd
                    );


                double peakProfit =
                    trade.MaxProfitUsd;


                double currentProfit =
                    trade.ProfitUsd;


                double giveback =
                    Math.Max(
                        0,
                        peakProfit - currentProfit
                    );


                // ========================================================
                // 2. DETERMINE RISK STAGE
                //
                // Protected:
                // Peak >= ProtectAtProfitUsd
                //
                // Trailing:
                // Peak >= TrailAtProfitUsd
                //
                // Dollar peak protection is NOT restricted to Trailing.
                // ========================================================

                RiskStage stage;


                if (peakProfit >=
                    profile.TrailAtProfitUsd)
                {
                    stage =
                        RiskStage.Trailing;
                }
                else if (peakProfit >=
                         profile.ProtectAtProfitUsd)
                {
                    stage =
                        RiskStage.Protected;
                }
                else if (peakProfit > 0)
                {
                    stage =
                        RiskStage.Developing;
                }
                else
                {
                    stage =
                        RiskStage.Initial;
                }


                // ========================================================
                // GOLD $8-$9 PROOF GAP: DEMO-ONLY RETURN-TO-ENTRY EXIT
                // ========================================================
                // The Protected stage starts at $8, but Gold Proof V3 only
                // arms its broker profit stop at $9. A trade peaking between
                // $8 and $9 can otherwise reverse into the hard-loss limit.
                // This covers ONLY that gap: do not change $9+ proof bands,
                // $18+ runners, or the existing initial-entry risk policy.
                // A software close is requested if profit falls to $0.50
                // after a demonstrated $8-$9 peak; broker execution can slip.
                // Backtest on demo before enabling in production.
                if (IsGold(s) &&
                    peakProfit >= 8.00 &&
                    peakProfit < 9.00 &&
                    currentProfit <= 0.50)
                {
                    string reason =
                        $"Gold $8-$9 proof gap exit | " +
                        $"peak={peakProfit:F2} | profit={currentProfit:F2} | " +
                        "floor=0.50 | software close (execution may slip)";
                    LogClose(trade, stage, reason);
                    return RiskDecision.Close(stage, reason);
                }

                // ========================================================
                // 3. HARD LOSS
                //
                // Most symbols continue to use the fixed HardLossUsd value.
                //
                // GOLD is allowed to breathe with H1 volatility:
                //
                // effective loss =
                //     MAX(fixed dollar floor, ATR-based dollar loss)
                //
                // then capped by MaxHardLossUsd.
                //
                // Example Gold profile:
                //
                // HardLossUsd      = $6
                // HardLossAtrFloor = 0.55 ATR
                // MaxHardLossUsd   = $10
                //
                // If 0.55 ATR is worth $8.20 for the actual trade volume,
                // the hard loss becomes $8.20.
                //
                // If 0.55 ATR is worth $13, the hard loss is capped at $10.
                //
                // IMPORTANT:
                // If tick metadata is unavailable, the engine safely falls
                // back to the original fixed HardLossUsd value.
                // ========================================================

                double effectiveHardLossUsd =
                    profile.HardLossUsd;


                double atrBasedHardLossUsd =
                    0.0;


                if (profile.HardLossAtrFloor > 0 &&
                    tickSize > 0 &&
                    tickValue > 0 &&
                    trade.Volume > 0)
                {
                    double hardLossPriceMove =
                        atr *
                        profile.HardLossAtrFloor;


                    atrBasedHardLossUsd =
                        PriceMoveToUsd(
                            trade,
                            hardLossPriceMove,
                            tickSize,
                            tickValue
                        );


                    if (atrBasedHardLossUsd > 0)
                    {
                        effectiveHardLossUsd =
                            Math.Max(
                                effectiveHardLossUsd,
                                atrBasedHardLossUsd
                            );
                    }
                }


                if (profile.MaxHardLossUsd > 0)
                {
                    effectiveHardLossUsd =
                        Math.Min(
                            effectiveHardLossUsd,
                            profile.MaxHardLossUsd
                        );
                }


                if (currentProfit <=
                    -effectiveHardLossUsd)
                {
                    string reason =
                        $"Hard loss limit | " +
                        $"profit={currentProfit:F2} | " +
                        $"effectiveLimit=-{effectiveHardLossUsd:F2} | " +
                        $"fixedFloor={profile.HardLossUsd:F2} | " +
                        $"atrFloor={profile.HardLossAtrFloor:F2} | " +
                        $"atrBasedUsd={atrBasedHardLossUsd:F2} | " +
                        $"maxCap={profile.MaxHardLossUsd:F2}";


                    LogClose(
                        trade,
                        stage,
                        reason
                    );


                    return RiskDecision.Close(
                        stage,
                        reason
                    );
                }

                // ========================================================
                // 3B. EARLY PROFIT PROTECTION
                //
                // Protect smaller wins during choppy / snap-back markets.
                //
                // This protection is active between:
                //
                // EarlyProtectAtProfitUsd
                // and
                // ProtectAtProfitUsd
                //
                // Example Gold:
                //
                // Peak = $5.00
                // EarlyLockPercent = 0.35
                //
                // Floor = $1.75
                //
                // If live profit falls to or below $1.75,
                // close the trade.
                //
                // Once ProtectAtProfitUsd is reached,
                // the normal stronger protection below takes over.
                // ========================================================

                bool earlyProtectionArmed =
                    profile.EarlyProtectAtProfitUsd > 0 &&
                    profile.EarlyLockPercent > 0 &&
                    peakProfit >= profile.EarlyProtectAtProfitUsd &&
                    peakProfit < profile.ProtectAtProfitUsd;


                double earlyProtectedProfitUsd =
                    0.0;


                if (earlyProtectionArmed)
                {
                    earlyProtectedProfitUsd =
                        peakProfit *
                        profile.EarlyLockPercent;


                    earlyProtectedProfitUsd =
                        Math.Max(
                            0,
                            earlyProtectedProfitUsd
                        );


                    if (profile.EnableEarlySoftwareClose &&
                        earlyProtectedProfitUsd > 0 &&
                        currentProfit <= earlyProtectedProfitUsd)
                    {
                        string reason =
                            $"Early profit protection | " +
                            $"peak={peakProfit:F2} | " +
                            $"profit={currentProfit:F2} | " +
                            $"giveback={giveback:F2} | " +
                            $"floor={earlyProtectedProfitUsd:F2} | " +
                            $"trigger={profile.EarlyProtectAtProfitUsd:F2} | " +
                            $"lockPct={profile.EarlyLockPercent:P0}";


                        LogClose(
                            trade,
                            stage,
                            reason
                        );


                        return RiskDecision.Close(
                            stage,
                            reason
                        );
                    }
                }

                // ========================================================
                // 4. UNIVERSAL PEAK-PROFIT PROTECTION
                //
                // MAJOR CHANGE:
                //
                // Peak protection begins once ProtectAtProfitUsd has
                // been reached.
                //
                // It DOES NOT wait for TrailAtProfitUsd.
                //
                // Gold:
                //
                // ProtectAtProfitUsd = $9
                // TrailAtProfitUsd   = $15
                //
                // So:
                //
                // $9+  -> dollar profit floor active
                // $15+ -> dollar floor + ATR runner
                // ========================================================

                double percentageFloor =
                    0.0;


                double maxGivebackFloor =
                    0.0;


                double protectedProfitUsd =
                    0.0;


                // Generic fixed peak protection remains active for non-Gold assets.
                //
                // Gold uses the health-aware winner-management logic later in this
                // method so that a structurally healthy H1 runner is not forced out
                // simply because a fixed percentage-of-peak floor was touched.
                bool peakProtectionArmed =
                    !IsGold(s) &&
                    !IsBnb(s) &&
                    peakProfit >=
                        profile.ProtectAtProfitUsd &&
                    (
                        profile.PeakProfitLockPercent > 0 ||
                        profile.MaxGivebackUsd > 0
                    );


                if (peakProtectionArmed)
                {
                    // ====================================================
                    // 4A. PERCENTAGE FLOOR
                    // ====================================================

                    if (profile.PeakProfitLockPercent > 0)
                    {
                        percentageFloor =
                            peakProfit *
                            profile.PeakProfitLockPercent;
                    }


                    // ====================================================
                    // 4B. MAXIMUM GIVEBACK FLOOR
                    // ====================================================

                    if (profile.MaxGivebackUsd > 0)
                    {
                        maxGivebackFloor =
                            peakProfit -
                            profile.MaxGivebackUsd;
                    }


                    // ====================================================
                    // 4C. USE THE STRONGEST FLOOR
                    // ====================================================

                    protectedProfitUsd =
                        Math.Max(
                            percentageFloor,
                            maxGivebackFloor
                        );


                    protectedProfitUsd =
                        Math.Max(
                            0,
                            protectedProfitUsd
                        );


                    // ====================================================
                    // 4D. SOFTWARE EMERGENCY PROFIT FLOOR
                    //
                    // CRITICAL:
                    //
                    // If live profit falls through the floor, CLOSE.
                    //
                    // Do NOT keep requesting SL modifications after the
                    // protected floor has already been violated.
                    // ====================================================

                    if (protectedProfitUsd > 0 &&
                        currentProfit <= protectedProfitUsd)
                    {
                        string reason =
                            $"Peak profit giveback protection | " +
                            $"peak={peakProfit:F2} | " +
                            $"profit={currentProfit:F2} | " +
                            $"giveback={giveback:F2} | " +
                            $"floor={protectedProfitUsd:F2} | " +
                            $"percentageFloor={percentageFloor:F2} | " +
                            $"givebackFloor={maxGivebackFloor:F2} | " +
                            $"lockPct=" +
                            $"{profile.PeakProfitLockPercent:P0} | " +
                            $"maxGiveback=" +
                            $"{profile.MaxGivebackUsd:F2}";


                        LogClose(
                            trade,
                            stage,
                            reason
                        );


                        return RiskDecision.Close(
                            stage,
                            reason
                        );
                    }
                }


                // ========================================================
                // 5. CURRENT EXECUTABLE LIVE PRICE
                //
                // BUY exits at BID.
                //
                // SELL exits at ASK.
                // ========================================================

                double currentPrice =
                    trade.Side == TradeSide.Buy
                        ? bid
                        : ask;

                // ========================================================
                // 5A. GOLD EARLY WINNER SAFETY FLOOR - DISABLED
                //
                // IMPORTANT LIVE-MARKET CORRECTION:
                //
                // Do NOT move Gold/XAU to a small positive stop merely because
                // peak profit reaches approximately $5-$8.
                //
                // A healthy H1 Gold continuation can make an initial push,
                // retrace deeply enough to revisit a small profit/breakeven
                // area, and then resume the trend. The former early-winner
                // floor could therefore stop a structurally healthy trade
                // before continuation.
                //
                // Gold protection remains controlled by:
                //
                //   1. hard-loss protection,
                //   2. failed-follow-through / early transition failure,
                //   3. EMA9 / EMA21 / adverse-candle structural evidence,
                //   4. Gold Proof V3 once the trade has genuinely proved
                //      itself,
                //   5. the later Gold runner/trailing stage.
                //
                // The profile fields are intentionally retained for source
                // compatibility, but the Gold profile sets them to zero.
                // There is intentionally NO automatic $5 early broker SL or
                // software-close floor here.
                // ========================================================


                // ========================================================
                // 5B. EARLY BROKER-SIDE PROFIT PROTECTION
                //
                // If early profit protection is armed, convert the
                // protected dollar floor into an actual broker SL.
                //
                // This runs while stage is still Developing.
                // ========================================================

                if (!IsGold(s) &&
                    earlyProtectionArmed &&
                    earlyProtectedProfitUsd > 0 &&
                    tickSize > 0 &&
                    tickValue > 0 &&
                    trade.Volume > 0)
                {
                    double earlyMinDistanceAtr =
                        Math.Max(
                            profile.MinStopDistanceAtr,
                            profile.EarlyBrokerMinDistanceAtr
                        );

                    double earlyAtrStopDistance =
                        atr * earlyMinDistanceAtr;

                    double earlyMinimumStopDistance =
                        Math.Max(
                            earlyAtrStopDistance,
                            brokerMinStopDistancePrice
                        );


                    double earlyDollarSL =
                        ProfitUsdToStopPrice(
                            trade,
                            earlyProtectedProfitUsd,
                            tickSize,
                            tickValue
                        );


                    double validEarlySL =
                        ValidateStopLoss(
                            trade,
                            earlyDollarSL,
                            bid,
                            ask,
                            earlyMinimumStopDistance
                        );


                    validEarlySL =
                        NormalizeStopLossToTick(
                            trade,
                            validEarlySL,
                            tickSize
                        );


                    // ========================================================
                    // GOLD EARLY-PROTECTION BREAKEVEN GUARD
                    //
                    // Once Gold has reached the early-profit protection stage,
                    // do NOT place a new broker-side stop on the losing side
                    // of the entry price.
                    //
                    // Why:
                    // Gold's EarlyBrokerMinDistanceAtr can force a requested
                    // profit-lock stop farther away from market. On H1 Gold,
                    // that can move the validated stop below entry for BUYs
                    // or above entry for SELLs.
                    //
                    // In that case we simply WAIT. The trade keeps breathing
                    // until price has moved far enough that the broker allows
                    // a breakeven-or-better stop.
                    //
                    // This guard is Gold-specific. Other asset classes retain
                    // their existing early-protection behavior.
                    // ========================================================

                    bool goldEarlyStopWouldLockLoss =
                        IsGold(s) &&
                        (
                            (trade.Side == TradeSide.Buy &&
                             validEarlySL < trade.EntryPrice)
                            ||
                            (trade.Side == TradeSide.Sell &&
                             validEarlySL > trade.EntryPrice)
                        );


                    if (goldEarlyStopWouldLockLoss)
                    {
                        LogFile.Write(
                            $"[{trade.Symbol}] 🟡 GOLD EARLY SL DEFERRED | " +
                            $"side={trade.Side} | " +
                            $"entry={trade.EntryPrice:F5} | " +
                            $"candidateSL={validEarlySL:F5} | " +
                            $"peak={peakProfit:F2} | " +
                            $"profit={currentProfit:F2} | " +
                            $"reason=validated early SL would still lock a loss; " +
                            $"waiting for breakeven-or-better broker stop"
                        );
                    }
                    else if (ShouldImproveStop(
                                 trade,
                                 validEarlySL,
                                 tickSize))
                    {
                        string reason =
                            $"Early profit lock | " +
                            $"peak={peakProfit:F2} | " +
                            $"profit={currentProfit:F2} | " +
                            $"protectedUsd={earlyProtectedProfitUsd:F2} | " +
                            $"lockPct={profile.EarlyLockPercent:P0} | " +
                            $"minDistanceATR={earlyMinDistanceAtr:F2} | " +
                            $"rawDollarSL={earlyDollarSL:F5}";


                        LogMoveSL(
                            trade,
                            stage,
                            validEarlySL,
                            reason
                        );


                        return RiskDecision.MoveSL(
                            stage,
                            validEarlySL,
                            reason
                        );
                    }
                }

                // ========================================================
                // 6. ADVERSE MOVE FROM ENTRY
                //
                // Uses executable live BID / ASK.
                // ========================================================

                double adverseMove =
                    trade.Side == TradeSide.Buy
                        ? trade.EntryPrice - currentPrice
                        : currentPrice - trade.EntryPrice;


                adverseMove =
                    Math.Max(
                        0,
                        adverseMove
                    );


                double adverseAtr =
                    adverseMove / atr;


                // ========================================================
                // 7. CLOSED CANDLE EVIDENCE
                // ========================================================

                var candle =
                    rates[^1];


                double candleRange =
                    candle.high -
                    candle.low;


                double candleBody =
                    Math.Abs(
                        candle.close -
                        candle.open
                    );


                double closedBodyRatio =
                    candleRange > 0
                        ? candleBody / candleRange
                        : 0;


                bool closedOppositeStrongCandle =
                    trade.Side == TradeSide.Buy

                        ? candle.close < candle.open &&
                          closedBodyRatio >=
                            profile.OppositeBodyRatio

                        : candle.close > candle.open &&
                          closedBodyRatio >=
                            profile.OppositeBodyRatio;


                // ========================================================
                // 8. LIVE ADVERSE CANDLE EVIDENCE
                //
                // Uses executable BID/ASK rather than waiting for candle
                // close.
                //
                // BUY:
                // BID below candle open = bearish live candle.
                //
                // SELL:
                // ASK above candle open = bullish live candle.
                // ========================================================

                double livePrice =
                    currentPrice;


                double liveHigh =
                    Math.Max(
                        candle.high,
                        livePrice
                    );


                double liveLow =
                    Math.Min(
                        candle.low,
                        livePrice
                    );


                double liveRange =
                    liveHigh -
                    liveLow;


                double liveBody =
                    Math.Abs(
                        livePrice -
                        candle.open
                    );


                double liveBodyRatio =
                    liveRange > 0
                        ? liveBody / liveRange
                        : 0;


                bool liveOppositeStrongCandle =
                    trade.Side == TradeSide.Buy

                        ? livePrice < candle.open &&
                          liveBodyRatio >=
                            profile.OppositeBodyRatio

                        : livePrice > candle.open &&
                          liveBodyRatio >=
                            profile.OppositeBodyRatio;


                // ========================================================
                // 9. COMBINED ADVERSE CANDLE EVIDENCE
                // ========================================================

                bool adverseCandleEvidence =
                    closedOppositeStrongCandle ||
                    liveOppositeStrongCandle;


                // ========================================================
                // 10. EMA VALUES
                // ========================================================

                double ema9 =
                    emaFast[^1];


                double ema21 =
                    emaSlow[^1];


                bool ema21AgainstTrade =
                    trade.Side == TradeSide.Buy
                        ? currentPrice < ema21
                        : currentPrice > ema21;


                // ========================================================
                // 10B. GOLD TRADE-HEALTH CLASSIFICATION
                //
                // The Gold winner manager is deliberately structure-aware.
                //
                // HEALTHY:
                // - executable price remains on the correct side of EMA9
                // - price remains on the correct side of EMA21
                // - EMA9 slope still agrees with the trade
                // - no strong opposite candle evidence is present
                //
                // WEAKENING:
                // - EMA9 alignment/slope deteriorates OR
                // - strong opposite candle evidence appears
                //
                // A decisive EMA21/structural failure is still handled by the
                // existing structural-failure logic below.
                // ========================================================

                bool isGold =
                    IsGold(s);


                double previousEma9 =
                    emaFast.Length >= 2
                        ? emaFast[^2]
                        : ema9;


                bool ema9SlopeAligned =
                    trade.Side == TradeSide.Buy
                        ? ema9 >= previousEma9
                        : ema9 <= previousEma9;


                bool priceOnCorrectSideOfEma9 =
                    trade.Side == TradeSide.Buy
                        ? currentPrice >= ema9
                        : currentPrice <= ema9;


                bool priceOnCorrectSideOfEma21 =
                    trade.Side == TradeSide.Buy
                        ? currentPrice >= ema21
                        : currentPrice <= ema21;


                bool goldHealthyRunner =
                    isGold &&
                    priceOnCorrectSideOfEma9 &&
                    priceOnCorrectSideOfEma21 &&
                    ema9SlopeAligned &&
                    !adverseCandleEvidence;


                bool goldWeakeningRunner =
                    isGold &&
                    !goldHealthyRunner &&
                    (
                        !priceOnCorrectSideOfEma9 ||
                        !ema9SlopeAligned ||
                        adverseCandleEvidence
                    );


                // ========================================================
                // BNB TRADE-HEALTH CLASSIFICATION
                //
                // BNB uses the same structural definition of "healthy" as
                // Gold, but with BNB-specific proof / runner thresholds.
                // ========================================================

                bool isBnb =
                    IsBnb(s);


                bool bnbHealthyRunner =
                    isBnb &&
                    priceOnCorrectSideOfEma9 &&
                    priceOnCorrectSideOfEma21 &&
                    ema9SlopeAligned &&
                    !adverseCandleEvidence;


                bool bnbWeakeningRunner =
                    isBnb &&
                    !bnbHealthyRunner &&
                    (
                        !priceOnCorrectSideOfEma9 ||
                        !ema9SlopeAligned ||
                        adverseCandleEvidence
                    );


                // ========================================================
                // 11. FAILED FOLLOW-THROUGH PROTECTION
                //
                // Continuation trades should make progress reasonably
                // quickly.
                //
                // Requires:
                //
                // 1. Enough bars elapsed.
                // 2. Weak peak profit.
                // 3. Meaningful EMA9 failure.
                // ========================================================

                bool isContinuationModel =
                    trade.Model ==
                        EntryModel.BreakoutContinuation ||

                    trade.Model ==
                        EntryModel.CompressionBreakoutContinuation ||

                    trade.Model ==
                        EntryModel.TrendRideContinuation;


                // ========================================================
                // IMPULSE MODEL DETECTION
                //
                // Do not hard-code a new EntryModel enum member here.
                // This preserves compatibility with the EntryModel enum
                // already used by the bot while still recognizing models
                // whose enum/name contains "Impulse".
                // ========================================================

                string entryModelName =
                    trade.Model.ToString();


                bool isImpulseModel =
                    !string.IsNullOrWhiteSpace(entryModelName) &&
                    entryModelName.IndexOf(
                        "Impulse",
                        StringComparison.OrdinalIgnoreCase
                    ) >= 0;


                // Impulse entries are continuation/momentum trades too,
                // so they must participate in failed-follow-through logic.
                bool usesFastFollowThroughProtection =
                    isContinuationModel ||
                    isImpulseModel;


                double ema9FailureDistance =
                    trade.Side == TradeSide.Buy
                        ? ema9 - currentPrice
                        : currentPrice - ema9;


                ema9FailureDistance =
                    Math.Max(
                        0,
                        ema9FailureDistance
                    );


                double ema9FailureAtr =
                    ema9FailureDistance / atr;


                bool noMeaningfulFollowThrough =
                    peakProfit <
                    profile.FollowThroughMinPeakProfitUsd;


                bool enoughBarsElapsed =
                    trade.CandlesSinceEntry >=
                    profile.FollowThroughBars;


                bool meaningfulEma9Failure =
                    ema9FailureAtr >=
                    profile.FollowThroughEma9FailureAtr;


                bool failedFollowThrough =
                    usesFastFollowThroughProtection &&
                    enoughBarsElapsed &&
                    noMeaningfulFollowThrough &&
                    meaningfulEma9Failure;


                if (failedFollowThrough)
                {
                    string reason =
                        $"Failed follow-through | " +
                        $"bars={trade.CandlesSinceEntry}/" +
                        $"{profile.FollowThroughBars} | " +
                        $"peak={peakProfit:F2}/" +
                        $"{profile.FollowThroughMinPeakProfitUsd:F2} | " +
                        $"ema9FailureATR=" +
                        $"{ema9FailureAtr:F2}/" +
                        $"{profile.FollowThroughEma9FailureAtr:F2}";


                    LogClose(
                        trade,
                        stage,
                        reason
                    );


                    return RiskDecision.Close(
                        stage,
                        reason
                    );
                }


                // ========================================================
                // 11A. GOLD EARLY TRANSITION FAILURE
                //
                // The Jan -> Sep Gold replay showed that some losing Gold
                // transition/continuation entries reveal failure early:
                //
                // - they never develop meaningful peak profit,
                // - they move materially adverse,
                // - executable price fails EMA9, and
                // - an opposite-direction candle is already visible.
                //
                // IMPORTANT:
                // The impulse/transition entry path currently reaches the trade
                // tracker as BreakoutContinuation, so this rule is deliberately
                // limited to that model, Gold, and the first two H1 bars.
                // It does NOT tighten Gold entries and does NOT alter mature
                // winner/runner management.
                // ========================================================

                bool goldEarlyFailureEnabled =
                    isGold &&
                    profile.GoldEarlyFailureMaxBars > 0 &&
                    profile.GoldEarlyFailureMaxPeakProfitUsd > 0 &&
                    profile.GoldEarlyFailureAdverseAtr > 0 &&
                    profile.GoldEarlyFailureEma9FailureAtr > 0;


                bool goldTransitionLikeModel =
                    trade.Model ==
                        EntryModel.BreakoutContinuation;


                bool goldEarlyTransitionFailure =
                    goldEarlyFailureEnabled &&
                    goldTransitionLikeModel &&
                    trade.CandlesSinceEntry <=
                        profile.GoldEarlyFailureMaxBars &&
                    peakProfit <
                        profile.GoldEarlyFailureMaxPeakProfitUsd &&
                    adverseAtr >=
                        profile.GoldEarlyFailureAdverseAtr &&
                    ema9FailureAtr >=
                        profile.GoldEarlyFailureEma9FailureAtr &&
                    adverseCandleEvidence;


                if (goldEarlyTransitionFailure)
                {
                    string reason =
                        $"Gold early transition failure | " +
                        $"bars={trade.CandlesSinceEntry}/" +
                        $"{profile.GoldEarlyFailureMaxBars} | " +
                        $"peak=${peakProfit:F2}/" +
                        $"${profile.GoldEarlyFailureMaxPeakProfitUsd:F2} | " +
                        $"adverseATR={adverseAtr:F2}/" +
                        $"{profile.GoldEarlyFailureAdverseAtr:F2} | " +
                        $"ema9FailureATR={ema9FailureAtr:F2}/" +
                        $"{profile.GoldEarlyFailureEma9FailureAtr:F2} | " +
                        $"oppositeEvidence={adverseCandleEvidence}";


                    LogClose(
                        trade,
                        stage,
                        reason
                    );


                    return RiskDecision.Close(
                        stage,
                        reason
                    );
                }


                // ========================================================
                // 11A2. GOLD FAST FAILURE - UNRESTRICTED S/R DEMO STUDY
                // ========================================================
                // A GOLD BreakoutContinuation entry with little/no favorable
                // progress may fail promptly when S/R is observation-only.
                // Do not wait for the existing 0.35 ATR early transition rule
                // if all independent failure evidence is already present.
                //
                // Scope is intentionally narrow:
                // - GOLD only; BreakoutContinuation only
                // - first H1 entry bar (CandlesSinceEntry 0 or 1)
                // - peak profit under $3 and trade currently losing
                // - adverse excursion at least 0.25 ATR
                // - executable price beyond EMA9 against the trade by 0.12 ATR
                // - strong opposite candle (closed or live)
                //
                // Existing hard loss, normal exits and winner management
                // are unchanged. This does not depend on new TrackedTrade
                // properties or change the public Evaluate signature.
                // ========================================================

                bool goldFastFailure =
                    isGold &&
                    goldTransitionLikeModel &&
                    trade.CandlesSinceEntry <= 1 &&
                    peakProfit < 3.00 &&
                    currentProfit < 0.0 &&
                    adverseAtr >= 0.25 &&
                    ema9FailureAtr >= 0.12 &&
                    adverseCandleEvidence;

                if (goldFastFailure)
                {
                    string reason =
                        $"Gold fast failure (S/R observation study) | " +
                        $"bars={trade.CandlesSinceEntry} | " +
                        $"peak=${peakProfit:F2}/$3.00 | " +
                        $"profit=${currentProfit:F2} | " +
                        $"adverseATR={adverseAtr:F2}/0.25 | " +
                        $"ema9FailureATR={ema9FailureAtr:F2}/0.12 | " +
                        $"oppositeCandle={adverseCandleEvidence}";

                    LogClose(trade, stage, reason);
                    return RiskDecision.Close(stage, reason);
                }


                // ========================================================
                // 11B. IMPULSE EMA9 REVERSAL FAILURE
                //
                // Purpose:
                // A strong impulse can make some initial profit and therefore
                // escape the weak-follow-through rule above. If that impulse
                // then reverses and a CLOSED candle decisively fails EMA9 in
                // the opposite direction, close the trade before waiting for
                // the much slower EMA21 structural-failure logic.
                //
                // BUY:
                // closed candle must close meaningfully BELOW EMA9.
                //
                // SELL:
                // closed candle must close meaningfully ABOVE EMA9.
                //
                // Also requires an opposite-direction closed candle with the
                // profile's existing body-ratio confirmation.
                // ========================================================

                double closedEma9FailureDistance =
                    trade.Side == TradeSide.Buy
                        ? ema9 - candle.close
                        : candle.close - ema9;


                closedEma9FailureDistance =
                    Math.Max(
                        0,
                        closedEma9FailureDistance
                    );


                double closedEma9FailureAtr =
                    closedEma9FailureDistance /
                    atr;


                bool closedMeaningfulEma9Failure =
                    closedEma9FailureAtr >=
                    profile.FollowThroughEma9FailureAtr;


                bool impulseReversalFailure =
                    isImpulseModel &&
                    trade.CandlesSinceEntry >= 1 &&
                    closedMeaningfulEma9Failure &&
                    closedOppositeStrongCandle;


                if (impulseReversalFailure)
                {
                    string reason =
                        $"Impulse EMA9 reversal failure | " +
                        $"model={entryModelName} | " +
                        $"bars={trade.CandlesSinceEntry} | " +
                        $"closedEMA9FailureATR=" +
                        $"{closedEma9FailureAtr:F2}/" +
                        $"{profile.FollowThroughEma9FailureAtr:F2} | " +
                        $"closedBody={closedBodyRatio:F2}/" +
                        $"{profile.OppositeBodyRatio:F2} | " +
                        $"close={candle.close:F5} | " +
                        $"ema9={ema9:F5}";


                    LogClose(
                        trade,
                        stage,
                        reason
                    );


                    return RiskDecision.Close(
                        stage,
                        reason
                    );
                }


                // ========================================================
                // 12. EMA21 FAILURE DISTANCE
                // ========================================================

                double ema21FailureDistance =
                    trade.Side == TradeSide.Buy
                        ? ema21 - currentPrice
                        : currentPrice - ema21;


                ema21FailureDistance =
                    Math.Max(
                        0,
                        ema21FailureDistance
                    );


                double ema21FailureAtr =
                    ema21FailureDistance /
                    atr;


                bool decisiveEma21Failure =
                    ema21AgainstTrade &&
                    ema21FailureAtr >=
                        profile.Ema21FailureAtr;


                // ========================================================
                // 13. TRUE STRUCTURAL FAILURE
                //
                // Requires:
                //
                // 1. Meaningful adverse movement
                // 2. Decisive EMA21 failure
                // 3. Adverse candle evidence
                // ========================================================

                bool meaningfulAdverseMove =
                    adverseAtr >=
                    profile.StructuralAdverseAtr;


                bool structuralFailure =
                    meaningfulAdverseMove &&
                    decisiveEma21Failure &&
                    adverseCandleEvidence;


                if (structuralFailure)
                {
                    string candleSource;


                    if (closedOppositeStrongCandle &&
                        liveOppositeStrongCandle)
                    {
                        candleSource =
                            "closed+live";
                    }
                    else if (liveOppositeStrongCandle)
                    {
                        candleSource =
                            "live";
                    }
                    else
                    {
                        candleSource =
                            "closed";
                    }


                    string reason =
                        $"Confirmed structural failure | " +

                        $"adverseATR={adverseAtr:F2}/" +
                        $"{profile.StructuralAdverseAtr:F2} | " +

                        $"ema21FailureATR=" +
                        $"{ema21FailureAtr:F2}/" +
                        $"{profile.Ema21FailureAtr:F2} | " +

                        $"closedBody={closedBodyRatio:F2} | " +

                        $"liveBody={liveBodyRatio:F2} | " +

                        $"requiredBody=" +
                        $"{profile.OppositeBodyRatio:F2} | " +

                        $"candleSource={candleSource}";


                    LogClose(
                        trade,
                        stage,
                        reason
                    );


                    return RiskDecision.Close(
                        stage,
                        reason
                    );
                }


                // ========================================================
                // 14. INITIAL / DEVELOPING
                //
                // Allow trades breathing room.
                // ========================================================

                if (stage == RiskStage.Initial ||
                    stage == RiskStage.Developing)
                {
                    LogHold(
                        trade,
                        stage,
                        currentPrice,
                        atr,
                        adverseAtr,
                        ema21FailureAtr,
                        closedBodyRatio,
                        liveBodyRatio,
                        closedOppositeStrongCandle,
                        liveOppositeStrongCandle,
                        peakProfit,
                        giveback,
                        "Trade developing - breathing room"
                    );


                    return RiskDecision.Hold(
                        stage,
                        "Trade developing"
                    );
                }


                // ========================================================
                // 15. MINIMUM VALID STOP DISTANCE
                // ========================================================

                double atrStopDistance =
                    atr *
                    profile.MinStopDistanceAtr;


                double minimumStopDistance =
                    Math.Max(
                        atrStopDistance,
                        brokerMinStopDistancePrice
                    );


                // ========================================================
                // 16. PROTECTED STAGE
                //
                // GOLD:
                // Winner management is health-aware.
                //
                // Healthy Gold trade:
                // - NO broker-side profit stop in the $8-$18 proof stage
                // - allow normal H1 Gold pullbacks
                //
                // Weakening Gold trade:
                // - existing structural / EMA failure logic remains active
                // - after a $12+ peak, a deterioration back to roughly
                //   breakeven can close the trade before it becomes a hard loss
                //
                // Other assets:
                // - retain the original fixed protection behaviour.
                // ========================================================

                if (stage ==
                    RiskStage.Protected)
                {
                    // ====================================================
                    // GOLD HEALTH-AWARE PROTECTED STAGE
                    // ====================================================

                    if (isGold)
                    {
                        // ====================================================
                        // GOLD PROOF STAGE V3
                        //
                        // GOAL:
                        // Keep Gold's H1 breathing room, but never allow a
                        // meaningful winner to remain protected only by the
                        // original losing SL.
                        //
                        // Behaviour:
                        //
                        // 1. Peak < $9
                        //      No broker profit stop is forced here.
                        //
                        // 2. Peak >= $9
                        //      Arm a meaningful stepped broker-side floor.
                        //      Healthy trades move through $3 / $5 / $7.50
                        //      proof bands rather than micro-ratcheting.
                        //
                        // 3. Weakening trades
                        //      Protect a little more aggressively.
                        //
                        // 4. Peak >= $12 and weakening back to approximately
                        //      breakeven
                        //      Keep the existing proof-stage failure exit.
                        //
                        // 5. The $18+ runner stage below still owns the wider
                        //      ATR trail and large-winner logic.
                        //
                        // This fixes the live failure observed on ticket
                        // 109375398, where Gold reached > $11 peak profit while
                        // the broker SL remained at the original losing level.
                        // ====================================================

                        const double proofBrokerProtectionArmUsd =
                            9.00;

                        // ----------------------------------------------------
                        // GOLD PROOF-STAGE STEP FLOORS
                        //
                        // Do not micro-ratchet the broker SL on every few cents
                        // of new peak profit.  Healthy Gold now advances through
                        // meaningful proof bands:
                        //
                        //   $9.00  - $11.99 peak -> protect $3.00
                        //   $12.00 - $14.99 peak -> protect $5.00
                        //   $15.00 - $17.99 peak -> protect $7.50
                        //
                        // At $18 the existing Gold runner stage takes over.
                        //
                        // This directly corrects the live XAUEUR case where a
                        // $13.58 peak protected only about $2.04.
                        // ----------------------------------------------------
                        const double proofBand1PeakUsd =
                            9.00;

                        const double proofBand1LockUsd =
                            3.00;

                        const double proofBand2PeakUsd =
                            12.00;

                        const double proofBand2LockUsd =
                            5.00;

                        const double proofBand3PeakUsd =
                            15.00;

                        const double proofBand3LockUsd =
                            7.50;

                        // If Gold is weakening during the proof stage, protect
                        // at least half of the demonstrated peak, while the
                        // healthy runner continues to use the stepped floors.
                        const double proofWeakeningLockPercent =
                            0.60;

                        // ----------------------------------------------------
                        // GOLD MID-WINNER CAPTURE CORRECTION
                        //
                        // The latest Gold continuation reached +$13.62 MFE but
                        // the fixed $5 proof-band floor allowed the realized
                        // result to fall back to about +$5.02.
                        //
                        // Keep the $9-$11.99 band unchanged for breathing room.
                        // Once peak >= $12, a healthy Gold winner has proved
                        // enough continuation to protect at least 50% of peak.
                        // If structure is weakening, protect at least 60%.
                        //
                        // This is deliberately below the later $18 runner stage
                        // and does not alter entry logic or hard-loss behavior.
                        // ----------------------------------------------------
                        const double proofHealthyMidWinnerLockPercent =
                            0.50;

                        const double proofWeakeningMidWinnerLockPercent =
                            0.60;

                        const double proofFailureArmUsd =
                            12.00;

                        const double proofFailureFloorUsd =
                            0.50;


                        bool proofBrokerProtectionArmed =
                            peakProfit >=
                            proofBrokerProtectionArmUsd;


                        bool proofFailureProtectionArmed =
                            peakProfit >=
                            proofFailureArmUsd;


                        bool proofStageFailedBackToBreakeven =
                            proofFailureProtectionArmed &&
                            goldWeakeningRunner &&
                            currentProfit <=
                                proofFailureFloorUsd;


                        if (proofStageFailedBackToBreakeven)
                        {
                            string reason =
                                $"Gold proof-stage failure exit | " +
                                $"peak={peakProfit:F2} | " +
                                $"profit={currentProfit:F2} | " +
                                $"arm={proofFailureArmUsd:F2} | " +
                                $"floor={proofFailureFloorUsd:F2} | " +
                                $"healthy={goldHealthyRunner} | " +
                                $"weakening={goldWeakeningRunner} | " +
                                $"ema9Aligned={priceOnCorrectSideOfEma9} | " +
                                $"ema9SlopeAligned={ema9SlopeAligned} | " +
                                $"oppositeEvidence={adverseCandleEvidence}";


                            LogClose(
                                trade,
                                stage,
                                reason
                            );


                            return RiskDecision.Close(
                                stage,
                                reason
                            );
                        }


                        // ----------------------------------------------------
                        // POSITIVE BROKER-SIDE FLOOR AFTER GOLD HAS PROVED ITSELF
                        // ----------------------------------------------------

                        if (proofBrokerProtectionArmed &&
                            tickSize > 0 &&
                            tickValue > 0 &&
                            trade.Volume > 0)
                        {
                            // Healthy Gold uses discrete proof bands rather
                            // than a continuously changing percentage.  This
                            // prevents broker-stop micro-ratcheting while still
                            // banking meaningful profit once the trade proves
                            // itself.
                            double goldProofLockUsd =
                                proofBand1LockUsd;


                            if (peakProfit >= proofBand3PeakUsd)
                            {
                                goldProofLockUsd =
                                    proofBand3LockUsd;
                            }
                            else if (peakProfit >= proofBand2PeakUsd)
                            {
                                goldProofLockUsd =
                                    proofBand2LockUsd;
                            }
                            else if (peakProfit >= proofBand1PeakUsd)
                            {
                                goldProofLockUsd =
                                    proofBand1LockUsd;
                            }


                            // ====================================================
                            // ADAPTIVE MID-WINNER CAPTURE
                            //
                            // Peak $9-$11.99:
                            //   retain the original fixed $3 proof floor.
                            //
                            // Peak $12-$17.99 and HEALTHY:
                            //   protect the stronger of the proof band or 50% peak.
                            //
                            // Peak $12-$17.99 and WEAKENING:
                            //   protect the stronger of the proof band or 60% peak.
                            //
                            // This prevents a proven $12-$18 Gold winner from
                            // surrendering most of its MFE while still leaving
                            // substantially more breathing room than a tight trail.
                            // ====================================================
                            if (peakProfit >= proofBand2PeakUsd)
                            {
                                double midWinnerLockPercent =
                                    goldWeakeningRunner
                                        ? proofWeakeningMidWinnerLockPercent
                                        : proofHealthyMidWinnerLockPercent;

                                goldProofLockUsd =
                                    Math.Max(
                                        goldProofLockUsd,
                                        peakProfit *
                                        midWinnerLockPercent
                                    );
                            }

                            // Weakening Gold below the $12 mid-winner threshold
                            // still retains the existing health-aware protection.
                            if (goldWeakeningRunner)
                            {
                                goldProofLockUsd =
                                    Math.Max(
                                        goldProofLockUsd,
                                        peakProfit *
                                        proofWeakeningLockPercent
                                    );
                            }


                            double goldProofDollarSL =
                                ProfitUsdToStopPrice(
                                    trade,
                                    goldProofLockUsd,
                                    tickSize,
                                    tickValue
                                );


                            double validGoldProofSL =
                                ValidateStopLoss(
                                    trade,
                                    goldProofDollarSL,
                                    bid,
                                    ask,
                                    minimumStopDistance
                                );


                            validGoldProofSL =
                                NormalizeStopLossToTick(
                                    trade,
                                    validGoldProofSL,
                                    tickSize
                                );


                            bool goldProofStopWouldLockLoss =
                                (trade.Side == TradeSide.Buy &&
                                 validGoldProofSL <= trade.EntryPrice)
                                ||
                                (trade.Side == TradeSide.Sell &&
                                 validGoldProofSL >= trade.EntryPrice);


                            if (!goldProofStopWouldLockLoss &&
                                ShouldImproveStop(
                                    trade,
                                    validGoldProofSL,
                                    tickSize))
                            {
                                string reason =
                                    $"Gold proof-stage positive SL | " +
                                    $"peak={peakProfit:F2} | " +
                                    $"profit={currentProfit:F2} | " +
                                    $"healthy={goldHealthyRunner} | " +
                                    $"weakening={goldWeakeningRunner} | " +
                                    $"lockUsd={goldProofLockUsd:F2} | " +
                                    $"midWinner={(peakProfit >= proofBand2PeakUsd)} | " +
                                    $"rawDollarSL={goldProofDollarSL:F5} | " +
                                    $"validatedSL={validGoldProofSL:F5} | " +
                                    $"arm={proofBrokerProtectionArmUsd:F2}";


                                LogMoveSL(
                                    trade,
                                    stage,
                                    validGoldProofSL,
                                    reason
                                );


                                return RiskDecision.MoveSL(
                                    stage,
                                    validGoldProofSL,
                                    reason
                                );
                            }


                            if (goldProofStopWouldLockLoss)
                            {
                                LogFile.Write(
                                    $"[{trade.Symbol}] 🟡 GOLD PROOF SL DEFERRED | " +
                                    $"side={trade.Side} | " +
                                    $"entry={trade.EntryPrice:F5} | " +
                                    $"candidateSL={validGoldProofSL:F5} | " +
                                    $"peak={peakProfit:F2} | " +
                                    $"profit={currentProfit:F2} | " +
                                    $"lockUsd={goldProofLockUsd:F2} | " +
                                    $"reason=broker minimum distance would place stop " +
                                    $"at breakeven-or-worse"
                                );
                            }
                        }


                        LogHold(
                            trade,
                            stage,
                            currentPrice,
                            atr,
                            adverseAtr,
                            ema21FailureAtr,
                            closedBodyRatio,
                            liveBodyRatio,
                            closedOppositeStrongCandle,
                            liveOppositeStrongCandle,
                            peakProfit,
                            giveback,
                            proofBrokerProtectionArmed
                                ? goldHealthyRunner
                                    ? "Gold proof stage V3 - healthy, positive broker floor armed"
                                    : "Gold proof stage V3 - weakening, stronger positive broker floor armed"
                                : goldHealthyRunner
                                    ? "Gold proof stage V3 - healthy, waiting for $9 proof"
                                    : "Gold proof stage V3 - weakening, structure logic remains in control"
                        );


                        return RiskDecision.Hold(
                            stage,
                            proofBrokerProtectionArmed
                                ? goldHealthyRunner
                                    ? "Gold healthy proof stage V3 - positive floor armed"
                                    : "Gold weakening proof stage V3 - positive floor armed"
                                : goldHealthyRunner
                                    ? "Gold healthy proof stage V3"
                                    : "Gold weakening proof stage V3"
                        );
                    }


                    // ====================================================
                    // BNB PROTECTED STAGE
                    //
                    // Data from Aug 3 -> Sep 18 showed that BNB still benefits
                    // from early profit protection, unlike Gold, but a 35% lock
                    // was too aggressive.  Once the trade reaches the $7
                    // protected stage, use only a small positive structural stop
                    // while it remains healthy.  If it weakens, tighten modestly.
                    // ====================================================

                    if (isBnb)
                    {
                        double bnbBaseLockAtr =
                            0.03;


                        double bnbProposedSL =
                            trade.Side == TradeSide.Buy
                                ? trade.EntryPrice +
                                  atr *
                                  bnbBaseLockAtr
                                : trade.EntryPrice -
                                  atr *
                                  bnbBaseLockAtr;


                        if (bnbWeakeningRunner)
                        {
                            double bnbWeakeningDistance =
                                atr *
                                0.40;


                            double bnbWeakeningSL =
                                trade.Side == TradeSide.Buy
                                    ? currentPrice -
                                      bnbWeakeningDistance
                                    : currentPrice +
                                      bnbWeakeningDistance;


                            if (trade.Side == TradeSide.Buy)
                            {
                                bnbProposedSL =
                                    Math.Max(
                                        bnbProposedSL,
                                        bnbWeakeningSL
                                    );
                            }
                            else
                            {
                                bnbProposedSL =
                                    Math.Min(
                                        bnbProposedSL,
                                        bnbWeakeningSL
                                    );
                            }
                        }


                        double validBnbSL =
                            ValidateStopLoss(
                                trade,
                                bnbProposedSL,
                                bid,
                                ask,
                                minimumStopDistance
                            );


                        validBnbSL =
                            NormalizeStopLossToTick(
                                trade,
                                validBnbSL,
                                tickSize
                            );


                        bool bnbStopWouldLockLoss =
                            (trade.Side == TradeSide.Buy &&
                             validBnbSL < trade.EntryPrice)
                            ||
                            (trade.Side == TradeSide.Sell &&
                             validBnbSL > trade.EntryPrice);


                        if (!bnbStopWouldLockLoss &&
                            ShouldImproveStop(
                                trade,
                                validBnbSL,
                                tickSize))
                        {
                            string reason =
                                $"BNB protected-stage SL | " +
                                $"peak={peakProfit:F2} | " +
                                $"profit={currentProfit:F2} | " +
                                $"healthy={bnbHealthyRunner} | " +
                                $"weakening={bnbWeakeningRunner} | " +
                                $"newSL={validBnbSL:F5}";


                            LogMoveSL(
                                trade,
                                stage,
                                validBnbSL,
                                reason
                            );


                            return RiskDecision.MoveSL(
                                stage,
                                validBnbSL,
                                reason
                            );
                        }


                        return RiskDecision.Hold(
                            stage,
                            bnbHealthyRunner
                                ? "BNB healthy protected stage"
                                : "BNB weakening protected stage"
                        );
                    }


                    // ====================================================
                    // ORIGINAL NON-GOLD PROTECTED STAGE
                    // ====================================================

                    double lockDistance =
                        atr *
                        profile.ProfitLockAtr;


                    double proposedSL =
                        trade.Side == TradeSide.Buy

                            ? trade.EntryPrice +
                              lockDistance

                            : trade.EntryPrice -
                              lockDistance;


                    bool canCalculateDollarSL =
                        peakProtectionArmed &&
                        protectedProfitUsd > 0 &&
                        tickSize > 0 &&
                        tickValue > 0 &&
                        trade.Volume > 0;


                    double dollarProfitFloorSL =
                        0;


                    if (canCalculateDollarSL)
                    {
                        dollarProfitFloorSL =
                            ProfitUsdToStopPrice(
                                trade,
                                protectedProfitUsd,
                                tickSize,
                                tickValue
                            );


                        if (trade.Side ==
                            TradeSide.Buy)
                        {
                            proposedSL =
                                Math.Max(
                                    proposedSL,
                                    dollarProfitFloorSL
                                );
                        }
                        else
                        {
                            proposedSL =
                                Math.Min(
                                    proposedSL,
                                    dollarProfitFloorSL
                                );
                        }
                    }


                    double validSL =
                        ValidateStopLoss(
                            trade,
                            proposedSL,
                            bid,
                            ask,
                            minimumStopDistance
                        );


                    validSL =
                        NormalizeStopLossToTick(
                            trade,
                            validSL,
                            tickSize
                        );


                    if (ShouldImproveStop(
                            trade,
                            validSL,
                            tickSize))
                    {
                        string reason =
                            $"Profit protection | " +
                            $"peak={peakProfit:F2} | " +
                            $"profit={currentProfit:F2} | " +
                            $"giveback={giveback:F2} | " +
                            $"protectedUsd={protectedProfitUsd:F2} | " +
                            $"dollarSL=" +
                            $"{(canCalculateDollarSL ? dollarProfitFloorSL.ToString("F5") : "N/A")}";


                        LogMoveSL(
                            trade,
                            stage,
                            validSL,
                            reason
                        );


                        return RiskDecision.MoveSL(
                            stage,
                            validSL,
                            reason
                        );
                    }


                    return RiskDecision.Hold(
                        stage,
                        "Protected"
                    );
                }


                // ========================================================
                // 17. TRAILING STAGE
                //
                // GOLD HEALTH-AWARE RUNNER:
                //
                // Healthy:
                // - wide 0.90 ATR trail
                // - no fixed 60%-of-peak floor
                // - minimum small positive floor only
                //
                // Weakening:
                // - tighten to 0.45 ATR
                // - protect the stronger of:
                //      60% of peak
                //      peak - $7
                //
                // This allows strong moves to breathe while still defending
                // accumulated profit when structure deteriorates.
                // ========================================================

                if (stage ==
                    RiskStage.Trailing)
                {
                    if (isGold)
                    {
                        // ====================================================
                        // GOLD RUNNER STAGE
                        //
                        // Runner begins only after the trade has demonstrated
                        // meaningful continuation.
                        //
                        // Healthy runner:
                        //   - wide 0.90 ATR trail
                        //
                        // Weakening runner:
                        //   - tighten to 0.45 ATR
                        //
                        // LARGE WINNER:
                        // Once peak >= $29, protect meaningful profit:
                        //
                        //   floor = max($20, peak - $10)
                        //
                        // Examples:
                        //   peak $29 -> floor $20
                        //   peak $40 -> floor $30
                        //   peak $60 -> floor $50
                        //
                        // This deliberately avoids choking the move before the
                        // trade has demonstrated true runner behaviour.
                        // ====================================================

                        double goldTrailAtr =
                            goldHealthyRunner
                                ? 0.90
                                : 0.45;


                        double goldTrailingDistance =
                            atr *
                            goldTrailAtr;


                        double goldTrailingSL =
                            trade.Side == TradeSide.Buy
                                ? currentPrice -
                                  goldTrailingDistance
                                : currentPrice +
                                  goldTrailingDistance;


                        // Mature runner should never be intentionally protected
                        // on the losing side of entry.
                        double breakevenPlusSL =
                            trade.Side == TradeSide.Buy
                                ? trade.EntryPrice +
                                  atr *
                                  0.03
                                : trade.EntryPrice -
                                  atr *
                                  0.03;


                        double goldProposedSL =
                            trade.Side == TradeSide.Buy
                                ? Math.Max(
                                    goldTrailingSL,
                                    breakevenPlusSL
                                  )
                                : Math.Min(
                                    goldTrailingSL,
                                    breakevenPlusSL
                                  );


                        // ====================================================
                        // LARGE-WINNER PROFIT FLOOR
                        // ====================================================

                        const double largeWinnerTriggerUsd =
                            29.00;


                        const double largeWinnerMinimumFloorUsd =
                            20.00;


                        const double largeWinnerMaxGivebackUsd =
                            10.00;


                        bool largeWinnerArmed =
                            peakProfit >=
                            largeWinnerTriggerUsd;


                        double largeWinnerFloorUsd =
                            0.0;


                        if (largeWinnerArmed)
                        {
                            largeWinnerFloorUsd =
                                Math.Max(
                                    largeWinnerMinimumFloorUsd,
                                    peakProfit -
                                    largeWinnerMaxGivebackUsd
                                );


                            // Software emergency protection in case price gaps
                            // through a requested broker stop.
                            if (currentProfit <=
                                largeWinnerFloorUsd)
                            {
                                string reason =
                                    $"Gold large-winner protection | " +
                                    $"peak={peakProfit:F2} | " +
                                    $"profit={currentProfit:F2} | " +
                                    $"floor={largeWinnerFloorUsd:F2} | " +
                                    $"trigger={largeWinnerTriggerUsd:F2} | " +
                                    $"maxGiveback={largeWinnerMaxGivebackUsd:F2}";


                                LogClose(
                                    trade,
                                    stage,
                                    reason
                                );


                                return RiskDecision.Close(
                                    stage,
                                    reason
                                );
                            }


                            if (tickSize > 0 &&
                                tickValue > 0 &&
                                trade.Volume > 0)
                            {
                                double largeWinnerDollarSL =
                                    ProfitUsdToStopPrice(
                                        trade,
                                        largeWinnerFloorUsd,
                                        tickSize,
                                        tickValue
                                    );


                                if (trade.Side == TradeSide.Buy)
                                {
                                    goldProposedSL =
                                        Math.Max(
                                            goldProposedSL,
                                            largeWinnerDollarSL
                                        );
                                }
                                else
                                {
                                    goldProposedSL =
                                        Math.Min(
                                            goldProposedSL,
                                            largeWinnerDollarSL
                                        );
                                }
                            }
                        }


                        double validGoldSL =
                            ValidateStopLoss(
                                trade,
                                goldProposedSL,
                                bid,
                                ask,
                                minimumStopDistance
                            );


                        validGoldSL =
                            NormalizeStopLossToTick(
                                trade,
                                validGoldSL,
                                tickSize
                            );


                        bool goldStopWouldLockLoss =
                            (trade.Side == TradeSide.Buy &&
                             validGoldSL < trade.EntryPrice)
                            ||
                            (trade.Side == TradeSide.Sell &&
                             validGoldSL > trade.EntryPrice);


                        if (!goldStopWouldLockLoss &&
                            ShouldImproveStop(
                                trade,
                                validGoldSL,
                                tickSize))
                        {
                            string reason =
                                $"Gold proof-runner trail | " +
                                $"peak={peakProfit:F2} | " +
                                $"profit={currentProfit:F2} | " +
                                $"healthy={goldHealthyRunner} | " +
                                $"weakening={goldWeakeningRunner} | " +
                                $"trailATR={goldTrailAtr:F2} | " +
                                $"largeWinner={largeWinnerArmed} | " +
                                $"largeFloor={largeWinnerFloorUsd:F2} | " +
                                $"newSL={validGoldSL:F5}";


                            LogMoveSL(
                                trade,
                                stage,
                                validGoldSL,
                                reason
                            );


                            return RiskDecision.MoveSL(
                                stage,
                                validGoldSL,
                                reason
                            );
                        }


                        LogHold(
                            trade,
                            stage,
                            currentPrice,
                            atr,
                            adverseAtr,
                            ema21FailureAtr,
                            closedBodyRatio,
                            liveBodyRatio,
                            closedOppositeStrongCandle,
                            liveOppositeStrongCandle,
                            peakProfit,
                            giveback,
                            largeWinnerArmed
                                ? $"Gold runner - large winner floor=${largeWinnerFloorUsd:F2}"
                                : goldHealthyRunner
                                    ? "Gold runner healthy - wide 0.90 ATR trail"
                                    : "Gold runner weakening - 0.45 ATR trail"
                        );


                        return RiskDecision.Hold(
                            stage,
                            largeWinnerArmed
                                ? "Gold large winner"
                                : goldHealthyRunner
                                    ? "Gold healthy runner"
                                    : "Gold weakening runner"
                        );
                    }


                    // ====================================================
                    // BNB HEALTH-AWARE RUNNER
                    //
                    // Runner begins at $12 peak profit.
                    //
                    // Healthy BNB runner:
                    //   - 0.90 ATR trail
                    //
                    // Weakening BNB runner:
                    //   - 0.50 ATR trail
                    //
                    // Large BNB winner:
                    //   - arm at $15 peak
                    //   - protect max($9, peak - $6)
                    //
                    // This preserves more of BNB's occasional large trend moves
                    // without changing the $6 hard-loss ceiling.
                    // ====================================================

                    if (isBnb)
                    {
                        double bnbTrailAtr =
                            bnbHealthyRunner
                                ? 0.90
                                : 0.50;


                        double bnbTrailingDistance =
                            atr *
                            bnbTrailAtr;


                        double bnbTrailingSL =
                            trade.Side == TradeSide.Buy
                                ? currentPrice -
                                  bnbTrailingDistance
                                : currentPrice +
                                  bnbTrailingDistance;


                        double bnbBreakevenPlusSL =
                            trade.Side == TradeSide.Buy
                                ? trade.EntryPrice +
                                  atr *
                                  0.03
                                : trade.EntryPrice -
                                  atr *
                                  0.03;


                        double bnbProposedSL =
                            trade.Side == TradeSide.Buy
                                ? Math.Max(
                                    bnbTrailingSL,
                                    bnbBreakevenPlusSL
                                  )
                                : Math.Min(
                                    bnbTrailingSL,
                                    bnbBreakevenPlusSL
                                  );


                        const double bnbLargeWinnerTriggerUsd =
                            15.00;


                        const double bnbLargeWinnerMinimumFloorUsd =
                            9.00;


                        const double bnbLargeWinnerMaxGivebackUsd =
                            6.00;


                        bool bnbLargeWinnerArmed =
                            peakProfit >=
                            bnbLargeWinnerTriggerUsd;


                        double bnbLargeWinnerFloorUsd =
                            0.0;


                        if (bnbLargeWinnerArmed)
                        {
                            bnbLargeWinnerFloorUsd =
                                Math.Max(
                                    bnbLargeWinnerMinimumFloorUsd,
                                    peakProfit -
                                    bnbLargeWinnerMaxGivebackUsd
                                );


                            if (currentProfit <=
                                bnbLargeWinnerFloorUsd)
                            {
                                string reason =
                                    $"BNB large-winner protection | " +
                                    $"peak={peakProfit:F2} | " +
                                    $"profit={currentProfit:F2} | " +
                                    $"floor={bnbLargeWinnerFloorUsd:F2}";


                                LogClose(
                                    trade,
                                    stage,
                                    reason
                                );


                                return RiskDecision.Close(
                                    stage,
                                    reason
                                );
                            }


                            if (tickSize > 0 &&
                                tickValue > 0 &&
                                trade.Volume > 0)
                            {
                                double bnbLargeWinnerDollarSL =
                                    ProfitUsdToStopPrice(
                                        trade,
                                        bnbLargeWinnerFloorUsd,
                                        tickSize,
                                        tickValue
                                    );


                                if (trade.Side == TradeSide.Buy)
                                {
                                    bnbProposedSL =
                                        Math.Max(
                                            bnbProposedSL,
                                            bnbLargeWinnerDollarSL
                                        );
                                }
                                else
                                {
                                    bnbProposedSL =
                                        Math.Min(
                                            bnbProposedSL,
                                            bnbLargeWinnerDollarSL
                                        );
                                }
                            }
                        }


                        double validBnbSL =
                            ValidateStopLoss(
                                trade,
                                bnbProposedSL,
                                bid,
                                ask,
                                minimumStopDistance
                            );


                        validBnbSL =
                            NormalizeStopLossToTick(
                                trade,
                                validBnbSL,
                                tickSize
                            );


                        bool bnbStopWouldLockLoss =
                            (trade.Side == TradeSide.Buy &&
                             validBnbSL < trade.EntryPrice)
                            ||
                            (trade.Side == TradeSide.Sell &&
                             validBnbSL > trade.EntryPrice);


                        if (!bnbStopWouldLockLoss &&
                            ShouldImproveStop(
                                trade,
                                validBnbSL,
                                tickSize))
                        {
                            string reason =
                                $"BNB health-aware runner | " +
                                $"peak={peakProfit:F2} | " +
                                $"profit={currentProfit:F2} | " +
                                $"healthy={bnbHealthyRunner} | " +
                                $"weakening={bnbWeakeningRunner} | " +
                                $"trailATR={bnbTrailAtr:F2} | " +
                                $"largeWinner={bnbLargeWinnerArmed} | " +
                                $"largeFloor={bnbLargeWinnerFloorUsd:F2} | " +
                                $"newSL={validBnbSL:F5}";


                            LogMoveSL(
                                trade,
                                stage,
                                validBnbSL,
                                reason
                            );


                            return RiskDecision.MoveSL(
                                stage,
                                validBnbSL,
                                reason
                            );
                        }


                        return RiskDecision.Hold(
                            stage,
                            bnbLargeWinnerArmed
                                ? "BNB large winner"
                                : bnbHealthyRunner
                                    ? "BNB healthy runner"
                                    : "BNB weakening runner"
                        );
                    }


                    // ====================================================
                    // ORIGINAL NON-GOLD TRAILING STAGE
                    // ====================================================

                    double trailingDistance =
                        atr *
                        profile.TrailDistanceAtr;


                    double trailingSL =
                        trade.Side == TradeSide.Buy

                            ? currentPrice -
                              trailingDistance

                            : currentPrice +
                              trailingDistance;


                    double atrProfitFloorSL =
                        trade.Side == TradeSide.Buy

                            ? trade.EntryPrice +
                              atr *
                              profile.ProfitLockAtr

                            : trade.EntryPrice -
                              atr *
                              profile.ProfitLockAtr;


                    bool canCalculateDollarSL =
                        peakProtectionArmed &&
                        protectedProfitUsd > 0 &&
                        tickSize > 0 &&
                        tickValue > 0 &&
                        trade.Volume > 0;


                    double dollarProfitFloorSL =
                        0;


                    if (canCalculateDollarSL)
                    {
                        dollarProfitFloorSL =
                            ProfitUsdToStopPrice(
                                trade,
                                protectedProfitUsd,
                                tickSize,
                                tickValue
                            );
                    }


                    double proposedSL;


                    if (trade.Side ==
                        TradeSide.Buy)
                    {
                        proposedSL =
                            Math.Max(
                                trailingSL,
                                atrProfitFloorSL
                            );


                        if (canCalculateDollarSL &&
                            dollarProfitFloorSL > 0)
                        {
                            proposedSL =
                                Math.Max(
                                    proposedSL,
                                    dollarProfitFloorSL
                                );
                        }
                    }
                    else
                    {
                        proposedSL =
                            Math.Min(
                                trailingSL,
                                atrProfitFloorSL
                            );


                        if (canCalculateDollarSL &&
                            dollarProfitFloorSL > 0)
                        {
                            proposedSL =
                                Math.Min(
                                    proposedSL,
                                    dollarProfitFloorSL
                                );
                        }
                    }


                    double validSL =
                        ValidateStopLoss(
                            trade,
                            proposedSL,
                            bid,
                            ask,
                            minimumStopDistance
                        );


                    validSL =
                        NormalizeStopLossToTick(
                            trade,
                            validSL,
                            tickSize
                        );


                    if (ShouldImproveStop(
                            trade,
                            validSL,
                            tickSize))
                    {
                        string reason =
                            $"Protected runner trail | " +
                            $"peak={peakProfit:F2} | " +
                            $"profit={currentProfit:F2} | " +
                            $"giveback={giveback:F2} | " +
                            $"protectedUsd={protectedProfitUsd:F2} | " +
                            $"trailATR={profile.TrailDistanceAtr:F2} | " +
                            $"dollarSL=" +
                            $"{(canCalculateDollarSL ? dollarProfitFloorSL.ToString("F5") : "N/A")}";


                        LogMoveSL(
                            trade,
                            stage,
                            validSL,
                            reason
                        );


                        return RiskDecision.MoveSL(
                            stage,
                            validSL,
                            reason
                        );
                    }


                    return RiskDecision.Hold(
                        stage,
                        "Runner"
                    );
                }


                return RiskDecision.Hold(
                    stage,
                    "No risk action"
                );
            }


            // ============================================================
            // CONVERT A PRICE MOVE INTO USD FOR THE ACTUAL TRADE VOLUME
            //
            // Used by the volatility-aware hard-loss calculation.
            //
            // TickValue = account-currency value of one TickSize move
            //             for ONE LOT.
            // ============================================================

            private static double PriceMoveToUsd(
                TrackedTrade trade,
                double priceMove,
                double tickSize,
                double tickValue)
            {
                if (trade == null)
                    return 0;


                if (priceMove <= 0 ||
                    tickSize <= 0 ||
                    tickValue <= 0 ||
                    trade.Volume <= 0)
                {
                    return 0;
                }


                double ticks =
                    priceMove /
                    tickSize;


                double usdPerTick =
                    tickValue *
                    trade.Volume;


                return
                    ticks *
                    usdPerTick;
            }


            // ============================================================
            // CONVERT USD PROFIT FLOOR INTO AN SL PRICE
            // ============================================================

            private static double ProfitUsdToStopPrice(
                TrackedTrade trade,
                double protectedProfitUsd,
                double tickSize,
                double tickValue)
            {
                if (trade == null)
                    return 0;


                if (protectedProfitUsd <= 0 ||
                    tickSize <= 0 ||
                    tickValue <= 0 ||
                    trade.Volume <= 0)
                {
                    return 0;
                }


                // ========================================================
                // TickValue is the account-currency value of one TickSize
                // move for ONE LOT.
                //
                // Scale by actual trade volume.
                // ========================================================

                double usdPerTick =
                    tickValue *
                    trade.Volume;


                if (usdPerTick <= 0)
                    return 0;


                double requiredTicks =
                    protectedProfitUsd /
                    usdPerTick;


                double requiredPriceMove =
                    requiredTicks *
                    tickSize;


                if (trade.Side ==
                    TradeSide.Buy)
                {
                    return
                        trade.EntryPrice +
                        requiredPriceMove;
                }


                return
                    trade.EntryPrice -
                    requiredPriceMove;
            }


            // ============================================================
            // STOP VALIDATION
            //
            // Ensures requested SL is not too close to the live market.
            // ============================================================

            private static double ValidateStopLoss(
                TrackedTrade trade,
                double proposedSL,
                double bid,
                double ask,
                double minimumDistance)
            {
                if (trade.Side ==
                    TradeSide.Buy)
                {
                    double highestAllowed =
                        bid -
                        minimumDistance;


                    return Math.Min(
                        proposedSL,
                        highestAllowed
                    );
                }


                double lowestAllowed =
                    ask +
                    minimumDistance;


                return Math.Max(
                    proposedSL,
                    lowestAllowed
                );
            }


            // ============================================================
            // NORMALIZE STOP LOSS TO BROKER TICK
            //
            // IMPORTANT:
            //
            // BUY SL is rounded DOWN.
            //
            // This prevents rounding from moving the SL closer to BID
            // than the broker allows.
            //
            // SELL SL is rounded UP.
            //
            // This prevents rounding from moving the SL closer to ASK
            // than the broker allows.
            // ============================================================

            private static double NormalizeStopLossToTick(
                TrackedTrade trade,
                double price,
                double tickSize)
            {
                if (price <= 0)
                    return 0;


                if (tickSize <= 0)
                    return price;


                double ticks =
                    price /
                    tickSize;


                if (trade.Side ==
                    TradeSide.Buy)
                {
                    return
                        Math.Floor(ticks) *
                        tickSize;
                }


                return
                    Math.Ceiling(ticks) *
                    tickSize;
            }


            // ============================================================
            // NEVER MOVE SL BACKWARDS
            //
            // MAJOR CHANGE:
            //
            // Require at least ONE REAL BROKER TICK of improvement.
            //
            // Prevents:
            //
            // 4613.00000 -> 4613.00175
            //
            // when actual tick size is 0.01.
            // ============================================================

            private static bool ShouldImproveStop(
                TrackedTrade trade,
                double newSL,
                double tickSize)
            {
                if (newSL <= 0)
                    return false;


                if (trade.StopLoss == 0)
                    return true;


                double minimumImprovement =
                    tickSize > 0
                        ? tickSize
                        : 0.0000001;


                // Tiny tolerance protects against floating-point
                // representation differences.
                double epsilon =
                    minimumImprovement *
                    0.001;


                if (trade.Side ==
                    TradeSide.Buy)
                {
                    return
                        newSL >=
                        trade.StopLoss +
                        minimumImprovement -
                        epsilon;
                }


                return
                    newSL <=
                    trade.StopLoss -
                    minimumImprovement +
                    epsilon;
            }


            // ============================================================
            // SYMBOL PROFILES
            // ============================================================

            private static Profile GetProfile(
                string symbol)
            {
                string s =
                    symbol?.ToUpperInvariant() ?? "";


                // ========================================================
                // GOLD
                //
                // IMPORTANT:
                //
                // $8:
                // Protected stage begins.
                //
                // $9:
                // Gold Proof V3 arms a small positive broker-side floor.
                //
                // $18:
                // ATR runner activates.
                // ========================================================

                if (IsGold(s))
                {
                    return new Profile
                    {
                        // ----------------------------------------------------
                        // GOLD VOLATILITY-AWARE HARD LOSS
                        //
                        // Keep $6 as the minimum emergency loss threshold, but
                        // do not force-close a normal H1 Gold pullback merely
                        // because $6 was reached before structure actually failed.
                        //
                        // Allow up to 0.55 ATR of adverse movement, converted to
                        // USD using the real tick value and actual trade volume.
                        //
                        // Absolute safety ceiling: $10.
                        // ----------------------------------------------------
                        HardLossUsd = 6.00,
                        HardLossAtrFloor = 0.55,
                        MaxHardLossUsd = 10.00,

                        StructuralAdverseAtr = 0.65,

                        Ema21FailureAtr = 0.20,

                        OppositeBodyRatio = 0.62,


                        // Protected stage begins at $8, but Gold Proof V3
                        // does not force a positive broker floor until peak >= $9.
                        ProtectAtProfitUsd = 8.00,

                        ProfitLockAtr = 0.02,


                        // True runner stage begins only after meaningful
                        // continuation has developed.
                        TrailAtProfitUsd = 18.00,

                        // Healthy Gold runners use the custom 0.90 ATR trail
                        // in the Gold-specific runner logic.
                        TrailDistanceAtr = 0.90,


                        // Keep at least 60% of peak profit.
                        PeakProfitLockPercent = 0.60,


                        // Never surrender more than $7
                        // once protection has armed.
                        MaxGivebackUsd = 7.00,


                        FollowThroughBars = 2,

                        FollowThroughMinPeakProfitUsd = 3.00,

                        FollowThroughEma9FailureAtr = 0.12,


                        // Gold-only early transition failure protection.
                        // Evidence-gated so normal pullbacks and mature runners
                        // are not touched.
                        GoldEarlyFailureMaxBars = 1,
                        GoldEarlyFailureMaxPeakProfitUsd = 10.00,
                        GoldEarlyFailureAdverseAtr = 0.35,
                        GoldEarlyFailureEma9FailureAtr = 0.12,


                        MinStopDistanceAtr = 0.10,

                        // ====================================================
                        // GENERIC EARLY PROTECTION
                        //
                        // Gold continues to bypass the generic early software
                        // close / broker stop. Gold uses its own modest early
                        // winner safety floor below.
                        // ====================================================
                        EarlyProtectAtProfitUsd = 3.00,
                        EarlyLockPercent = 0.35,

                        EarlyBrokerMinDistanceAtr = 0.35,
                        EnableEarlySoftwareClose = false,

                        // ====================================================
                        // GOLD EARLY WINNER SAFETY
                        //
                        // Arm only after Gold has demonstrated at least $5.
                        // Lock 20% of peak, with a minimum $1 floor and a
                        // maximum $2 floor before normal Gold protection.
                        //
                        // Examples:
                        //   Peak $5.00  -> lock $1.00
                        //   Peak $6.75  -> lock $1.35
                        //   Peak $7.50  -> lock $1.50
                        //
                        // ProtectAtProfitUsd ($8) and Gold Proof V3 ($9)
                        // continue to own the later stages.
                        // ====================================================
                        // DISABLED:
                        // Live Gold showed that a healthy H1 continuation can
                        // reach roughly +$5/+6, retrace normally, and then resume.
                        // Do not force a positive broker stop merely because a
                        // small early profit peak was reached.
                        GoldEarlyWinnerArmUsd = 0.00,
                        GoldEarlyWinnerLockPercent = 0.00,
                        GoldEarlyWinnerMinLockUsd = 0.00,
                        GoldEarlyWinnerMaxLockUsd = 0.00,

                    };
                }


                // ========================================================
                // BTC / ETH
                // ========================================================

                if (s.Contains("BTC") ||
                    s.Contains("ETH"))
                {
                    return new Profile
                    {
                        HardLossUsd = 7.00,

                        StructuralAdverseAtr = 0.80,

                        Ema21FailureAtr = 0.20,

                        OppositeBodyRatio = 0.60,

                        ProtectAtProfitUsd = 8.00,

                        ProfitLockAtr = 0.05,

                        TrailAtProfitUsd = 15.00,

                        TrailDistanceAtr = 1.00,

                        PeakProfitLockPercent = 0.00,

                        MaxGivebackUsd = 0.00,

                        FollowThroughBars = 2,

                        FollowThroughMinPeakProfitUsd = 3.00,

                        FollowThroughEma9FailureAtr = 0.12,

                        // ----------------------------------------------------
                        // BTC / ETH EARLY PROFIT PROTECTION
                        //
                        // Arm early protection at a modest profit, but do not
                        // software-close on an ordinary H1 pullback.
                        // Require at least 0.35 ATR breathing room.
                        // ----------------------------------------------------
                        EarlyProtectAtProfitUsd = 3.50,
                        EarlyLockPercent = 0.30,

                        EarlyBrokerMinDistanceAtr = 0.35,
                        EnableEarlySoftwareClose = false,

                        MinStopDistanceAtr = 0.10
                    };
                }


                // ========================================================
                // BNB
                //
                // Aug 3 -> Sep 18 BNB replay:
                // - keep the $6 hard-loss ceiling
                // - delay early protection slightly to $3
                // - reduce early lock from 35% to 25%
                // - protected stage remains $7
                // - runner stage remains $12
                // - runner management is handled by the BNB-specific
                //   health-aware branch above.
                // ========================================================

                if (IsBnb(s))
                {
                    return new Profile
                    {
                        HardLossUsd = 6.00,

                        StructuralAdverseAtr = 0.75,

                        Ema21FailureAtr = 0.18,

                        OppositeBodyRatio = 0.60,

                        ProtectAtProfitUsd = 7.00,

                        ProfitLockAtr = 0.03,

                        TrailAtProfitUsd = 12.00,

                        TrailDistanceAtr = 0.90,

                        // BNB's fixed percentage peak lock is intentionally
                        // bypassed.  The BNB runner branch handles healthy vs.
                        // weakening trades and large-winner protection.
                        PeakProfitLockPercent = 0.60,

                        MaxGivebackUsd = 6.00,

                        FollowThroughBars = 2,

                        FollowThroughMinPeakProfitUsd = 3.00,

                        FollowThroughEma9FailureAtr = 0.12,

                        EarlyProtectAtProfitUsd = 3.00,

                        EarlyLockPercent = 0.25,

                        EarlyBrokerMinDistanceAtr = 0.30,

                        EnableEarlySoftwareClose = false,

                        MinStopDistanceAtr = 0.10
                    };
                }


                // ========================================================
                // SOL / XRP / ADA / OTHER CRYPTO
                // ========================================================

                if (IsCrypto(s))
                {
                    return new Profile
                    {
                        HardLossUsd = 6.00,

                        StructuralAdverseAtr = 0.75,

                        Ema21FailureAtr = 0.18,

                        OppositeBodyRatio = 0.60,

                        ProtectAtProfitUsd = 7.00,

                        ProfitLockAtr = 0.05,

                        TrailAtProfitUsd = 12.00,

                        TrailDistanceAtr = 0.75,

                        PeakProfitLockPercent = 0.60,

                        MaxGivebackUsd = 6.00,

                        FollowThroughBars = 2,

                        FollowThroughMinPeakProfitUsd = 3.00,

                        FollowThroughEma9FailureAtr = 0.12,

                        // ----------------------------------------------------
                        // BNB / SOL / XRP / ADA / OTHER CRYPTO
                        //
                        // Early protection is active, but the physical broker
                        // stop must stay at least 0.30 ATR away from live price.
                        // The early dollar floor does not force a software close.
                        // ----------------------------------------------------
                        EarlyProtectAtProfitUsd = 0.75,
                        EarlyLockPercent = 0.35,

                        EarlyBrokerMinDistanceAtr = 0.30,
                        EnableEarlySoftwareClose = false,

                        MinStopDistanceAtr = 0.10
                    };
                }


                // ========================================================
                // CASH INDICES
                //
                // Explicit branch for broker symbols such as:
                // US100.cash, US30.cash, US500.cash, GER40.cash, UK100.cash.
                //
                // Starts with the existing validated index profile so CASH
                // symbols are isolated and can be tuned independently later
                // without changing FX, Gold, BNB or non-cash indices.
                // ========================================================

                if (IsCashIndex(s))
                {
                    return new Profile
                    {
                        // ====================================================
                        // US100.CASH VALIDATED PROFILE
                        //
                        // Aug 3 -> Sep 18 M1/H1 replay:
                        // - hard-loss ceiling = $1.25
                        // - protection begins at $0.75
                        // - runner begins at $1.75
                        // - runner trail remains 1.00 ATR
                        // - no early software-profit lock
                        //
                        // This profile was selected because it remained
                        // positive in the earlier and later date splits and
                        // across 2h / 3h / 4h / 6h holding windows.
                        // ====================================================

                        HardLossUsd = 1.25,

                        StructuralAdverseAtr = 0.75,

                        Ema21FailureAtr = 0.18,

                        OppositeBodyRatio = 0.60,

                        ProtectAtProfitUsd = 2.50,

                        ProfitLockAtr = 0.05,

                        TrailAtProfitUsd = 1.75,

                        TrailDistanceAtr = 0.80,

                        PeakProfitLockPercent = 0.00,

                        MaxGivebackUsd = 0.00,

                        FollowThroughBars = 2,

                        FollowThroughMinPeakProfitUsd = 1.00,

                        FollowThroughEma9FailureAtr = 0.12,

                        // No early dollar lock for CASH.
                        EarlyProtectAtProfitUsd = 0.00,

                        EarlyLockPercent = 0.00,

                        EarlyBrokerMinDistanceAtr = 0.10,

                        EnableEarlySoftwareClose = false,

                        MinStopDistanceAtr = 0.10
                    };
                }


                // ========================================================
                // INDICES
                // ========================================================

                if (IsIndex(s))
                {
                    return new Profile
                    {
                        HardLossUsd = 6.00,

                        StructuralAdverseAtr = 0.75,

                        Ema21FailureAtr = 0.18,

                        OppositeBodyRatio = 0.60,

                        ProtectAtProfitUsd = 8.00,

                        ProfitLockAtr = 0.05,

                        TrailAtProfitUsd = 12.00,

                        TrailDistanceAtr = 0.65,

                        PeakProfitLockPercent = 0.00,

                        MaxGivebackUsd = 0.00,

                        FollowThroughBars = 2,

                        FollowThroughMinPeakProfitUsd = 3.00,

                        FollowThroughEma9FailureAtr = 0.12,

                        EarlyBrokerMinDistanceAtr = 0.10,
                        EnableEarlySoftwareClose = true,

                        MinStopDistanceAtr = 0.10
                    };
                }


                // ========================================================
                // FX
                //
                // EURUSD Aug 3 -> Sep 18 replay validation:
                // Keep the existing FX profile.
                //
                // The provisional FX proof-runner tested:
                //   early protect $3 / 25%
                //   protected stage $5
                //   runner $8
                //   health-aware 0.65 / 0.35 ATR trail
                //
                // It reduced expectancy on the reconstructed EURUSD
                // Transition sample, so those provisional rules are NOT
                // enabled here.
                // ========================================================

                return new Profile
                {
                    HardLossUsd = 5.00,

                    StructuralAdverseAtr = 0.65,

                    Ema21FailureAtr = 0.15,

                    OppositeBodyRatio = 0.58,

                    ProtectAtProfitUsd = 5.00,

                    ProfitLockAtr = 0.04,

                    TrailAtProfitUsd = 8.00,

                    TrailDistanceAtr = 0.55,

                    PeakProfitLockPercent = 0.00,

                    MaxGivebackUsd = 0.00,

                    FollowThroughBars = 2,

                    FollowThroughMinPeakProfitUsd = 2.00,

                    FollowThroughEma9FailureAtr = 0.10,

                    EarlyBrokerMinDistanceAtr = 0.08,
                    EnableEarlySoftwareClose = true,

                    MinStopDistanceAtr = 0.08
                };
            }


            // ============================================================
            // SYMBOL HELPERS
            // ============================================================

            private static bool IsGold(
                string symbol)
            {
                return
                    symbol.Contains("XAU") ||
                    symbol.Contains("XAG") ||
                    symbol.Contains("GOLD") ||
                    symbol.Contains("SILVER");
            }


            private static bool IsBnb(
                string symbol)
            {
                return
                    symbol.Contains("BNB");
            }


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
                    symbol.Contains("LTC") ||
                    symbol.Contains("AVAX") ||
                    symbol.Contains("LINK");
            }


            private static bool IsCashIndex(
                string symbol)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    return false;
                }


                string s =
                    symbol.ToUpperInvariant();


                return
                    s.Contains(".CASH") ||
                    s.EndsWith("CASH");
            }


            private static bool IsIndex(
                string symbol)
            {
                return
                    symbol.Contains("US100") ||
                    symbol.Contains("NAS") ||
                    symbol.Contains("USTEC") ||
                    symbol.Contains("US30") ||
                    symbol.Contains("DJ") ||
                    symbol.Contains("US500") ||
                    symbol.Contains("SPX") ||
                    symbol.Contains("GER") ||
                    symbol.Contains("DAX") ||
                    symbol.Contains("UK100") ||
                    symbol.Contains("JP225") ||
                    symbol.Contains("INDEX") ||
                    symbol.Contains(".CASH");
            }


            // ============================================================
            // LOGGING
            // ============================================================

            private static void LogClose(
                TrackedTrade trade,
                RiskStage stage,
                string reason)
            {
                LogFile.Write(
                    $"[{trade.Symbol}] 🛑 UNIFIED RISK CLOSE | " +
                    $"stage={stage} | " +
                    $"side={trade.Side} | " +
                    $"profit={trade.ProfitUsd:F2} | " +
                    $"peak={trade.MaxProfitUsd:F2} | " +
                    $"reason={reason}"
                );
            }


            private static void LogMoveSL(
                TrackedTrade trade,
                RiskStage stage,
                double newSL,
                string reason)
            {
                LogFile.Write(
                    $"[{trade.Symbol}] 🔒 UNIFIED RISK SL | " +
                    $"stage={stage} | " +
                    $"side={trade.Side} | " +
                    $"profit={trade.ProfitUsd:F2} | " +
                    $"peak={trade.MaxProfitUsd:F2} | " +
                    $"oldSL={trade.StopLoss:F5} → " +
                    $"newSL={newSL:F5} | " +
                    $"reason={reason}"
                );
            }


            private static void LogHold(
                TrackedTrade trade,
                RiskStage stage,
                double currentPrice,
                double atr,
                double adverseAtr,
                double ema21FailureAtr,
                double closedBodyRatio,
                double liveBodyRatio,
                bool closedOpposite,
                bool liveOpposite,
                double peakProfit,
                double giveback,
                string reason)
            {
                LogFile.Write(
                    $"[{trade.Symbol}] 🟢 UNIFIED RISK HOLD | " +
                    $"stage={stage} | " +
                    $"side={trade.Side} | " +
                    $"price={currentPrice:F5} | " +
                    $"profit={trade.ProfitUsd:F2} | " +
                    $"peak={peakProfit:F2} | " +
                    $"giveback={giveback:F2} | " +
                    $"ATR={atr:F5} | " +
                    $"adverseATR={adverseAtr:F2} | " +
                    $"ema21FailureATR={ema21FailureAtr:F2} | " +
                    $"closedBody={closedBodyRatio:F2} | " +
                    $"liveBody={liveBodyRatio:F2} | " +
                    $"closedOpposite={closedOpposite} | " +
                    $"liveOpposite={liveOpposite} | " +
                    $"reason={reason}"
                );
            }
        }
    }
}