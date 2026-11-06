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
            }


            // GOLD profit preservation policy (account currency USD assumed).
            // The levels are based on observed PEAK profit, not current profit.
            // The broker SL is requested separately; software closes are a fallback.
            private static double GoldProfitFloorUsd(double peakUsd)
            {
                if (peakUsd >= 18.00) return Math.Max(10.00, peakUsd - 8.00);
                if (peakUsd >= 13.00) return 6.00;
                if (peakUsd >= 10.00) return 4.00;
                if (peakUsd >= 8.00) return 2.00;
                return 0.00;
            }

            // Diagnostic-only, per tracked trade. Weak keys avoid retaining closed trades.
            private sealed class GoldStudyState
            {
                public int MilestoneMask;
                public bool ReturnedBelowZero;
            }

            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
                TrackedTrade, GoldStudyState> GoldStudyStates =
                new System.Runtime.CompilerServices.ConditionalWeakTable<TrackedTrade, GoldStudyState>();

            private static void RecordGoldProfitStudy(TrackedTrade trade)
            {
                if (trade == null || !IsGold(trade.Symbol)) return;
                double peak = trade.MaxProfitUsd;
                double profit = trade.ProfitUsd;
                if (double.IsNaN(peak) || double.IsInfinity(peak) ||
                    double.IsNaN(profit) || double.IsInfinity(profit)) return;

                GoldStudyState state = GoldStudyStates.GetValue(trade, _ => new GoldStudyState());
                lock (state)
                {
                    double[] milestones = { 2.0, 3.0, 5.0, 8.0 };
                    for (int i = 0; i < milestones.Length; i++)
                    {
                        int flag = 1 << i;
                        if (peak >= milestones[i] && (state.MilestoneMask & flag) == 0)
                        {
                            state.MilestoneMask |= flag;
                            LogFile.Write($"[{trade.Symbol}] GOLD PROFIT STUDY | " +
                                $"event=PEAK_MILESTONE | thresholdUsd={milestones[i]:F2} | " +
                                $"side={trade.Side} | entry={trade.EntryPrice:F5} | " +
                                $"volume={trade.Volume:F2} | peakUsd={peak:F2} | " +
                                $"currentUsd={profit:F2}");
                        }
                    }
                    if (!state.ReturnedBelowZero && peak >= 2.0 && profit < 0.0)
                    {
                        state.ReturnedBelowZero = true;
                        LogFile.Write($"[{trade.Symbol}] GOLD PROFIT STUDY | " +
                            $"event=RETURNED_BELOW_ZERO | side={trade.Side} | " +
                            $"entry={trade.EntryPrice:F5} | volume={trade.Volume:F2} | " +
                            $"peakUsd={peak:F2} | currentUsd={profit:F2} | " +
                            $"givebackUsd={(peak - profit):F2}");
                    }
                }
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


                // ========================================================
                // GOLD EMERGENCY HARD-LOSS FAILSAFE
                // ========================================================
                // Evaluate this BEFORE the indicator-data guard. Missing H1
                // rates or EMA arrays must not disable GOLD loss protection.
                // Request an early software close at $6, rather than waiting
                // for the absolute $7 risk ceiling. This is intentionally
                // independent of ATR, structure, and the entry model.
                //
                // Use position P/L supplied by the caller. A RiskDecision.Close
                // is only an instruction to the caller, not a filled order.
                // Broker-side SL and verified execution remain essential:
                // software closes can slip or fail during disconnection.
                if (IsGold(symbol) &&
                    !double.IsNaN(trade.ProfitUsd) &&
                    !double.IsInfinity(trade.ProfitUsd))
                {
                    Profile goldSafetyProfile = GetProfile(symbol);
                    // $6 early close request gives a buffer before the $7
                    // configured ceiling. ATR must never delay this failsafe.
                    double emergencyLossLimit = goldSafetyProfile.HardLossUsd;
                    if (goldSafetyProfile.MaxHardLossUsd > 0)
                    {
                        emergencyLossLimit = Math.Min(
                            emergencyLossLimit,
                            goldSafetyProfile.MaxHardLossUsd);
                    }

                    if (emergencyLossLimit > 0 &&
                        trade.ProfitUsd <= -emergencyLossLimit)
                    {
                        string emergencyReason =
                            $"GOLD emergency loss failsafe (independent of indicator data) | " +
                            $"profit={trade.ProfitUsd:F2} | " +
                            $"earlyTrigger=-{emergencyLossLimit:F2} | " +
                            $"configuredCeiling=-{goldSafetyProfile.MaxHardLossUsd:F2}";

                        LogClose(trade, RiskStage.Initial, emergencyReason);
                        return RiskDecision.Close(RiskStage.Initial, emergencyReason);
                    }
                }

                // GOLD profit milestones are used to request broker-side SL only.
                // Do NOT software-close merely because observed profit crossed a floor.
                // The existing hard-loss and structural-failure exits are unchanged.
                if (IsGold(symbol) &&
                    !double.IsNaN(trade.ProfitUsd) &&
                    !double.IsInfinity(trade.ProfitUsd))
                {
                    trade.MaxProfitUsd = Math.Max(trade.MaxProfitUsd, trade.ProfitUsd);
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


                // Record peak milestones without influencing any risk decision.
                RecordGoldProfitStudy(trade);

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
                // 3. HARD LOSS
                //
                // Most symbols continue to use the fixed HardLossUsd value.
                //
                // Existing ATR-aware calculation is retained for compatibility:
                // effective loss = MIN(MAX(fixed floor, ATR USD), maximum cap).
                //
                // GOLD IMPORTANT: the emergency check above is evaluated first
                // and requests a close at $6, regardless of ATR or indicators.
                // The profile's configured absolute ceiling is now $7.
                // Thus this later ATR calculation cannot postpone GOLD's
                // early emergency close request.
                //
                // Other symbols keep their original hard-loss logic.
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


                // ========================================================
                // CASH DEMO: FIRST-BAR FAST FAILURE
                // The SR observation trial permits more entries. Exit an
                // unproven CASH momentum/continuation trade only when price,
                // EMA9 and an opposite candle ALL confirm failure.
                // Original hard loss and winner/trailing rules remain intact.
                // ========================================================
                bool cashFastFailure =
                    IsCashIndex(s) &&
                    usesFastFollowThroughProtection &&
                    trade.CandlesSinceEntry <= 1 &&
                    peakProfit < 0.40 &&
                    currentProfit < 0.0 &&
                    adverseAtr >= 0.25 &&
                    ema9FailureAtr >= 0.12 &&
                    adverseCandleEvidence;

                if (cashFastFailure)
                {
                    string reason =
                        $"CASH fast failure (demo) | model={entryModelName} | " +
                        $"bars={trade.CandlesSinceEntry} | " +
                        $"profit={currentProfit:F2} | peak={peakProfit:F2} | " +
                        $"adverseATR={adverseAtr:F2}/0.25 | " +
                        $"ema9FailureATR={ema9FailureAtr:F2}/0.12 | " +
                        $"oppositeCandle={adverseCandleEvidence}";
                    LogClose(trade, stage, reason);
                    return RiskDecision.Close(stage, reason);
                }

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
                // Gold peak-profit floors: $8->$2, $10->$4, $13->$6,
                // $18->$10, then peak-$8. Broker SL must be accepted and
                // verified by the calling executor. Software fallback closes
                // once observed profit falls through the armed floor.
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
                        double floorUsd = GoldProfitFloorUsd(peakProfit);
                        bool hasTickValue = tickSize > 0 && tickValue > 0 && trade.Volume > 0;

                        if (!hasTickValue)
                        {
                            LogFile.Write($"[{trade.Symbol}] GOLD PROFIT SL NOT CALCULABLE | " +
                                $"peak={peakProfit:F2} | floor={floorUsd:F2} | " +
                                $"tickSize={tickSize} | tickValue={tickValue} | volume={trade.Volume} | " +
                                "software floor remains active; check broker SL manually");
                            return RiskDecision.Hold(stage, "Gold profit floor active; broker tick metadata unavailable");
                        }

                        double requestedSL = ProfitUsdToStopPrice(
                            trade, floorUsd, tickSize, tickValue);
                        double goldProtectedValidSL = NormalizeStopLossToTick(
                            trade,
                            ValidateStopLoss(trade, requestedSL, bid, ask, minimumStopDistance),
                            tickSize);

                        // Never claim a dollar floor is guaranteed if broker stop
                        // rules force the actual stop below the requested price.
                        bool stillInProfit = trade.Side == TradeSide.Buy
                            ? goldProtectedValidSL > trade.EntryPrice
                            : goldProtectedValidSL < trade.EntryPrice;
                        bool meetsRequestedFloor = trade.Side == TradeSide.Buy
                            ? goldProtectedValidSL + tickSize * 0.001 >= requestedSL
                            : goldProtectedValidSL - tickSize * 0.001 <= requestedSL;

                        if (stillInProfit && ShouldImproveStop(trade, goldProtectedValidSL, tickSize))
                        {
                            string reason = $"GOLD PROTECTED PEAK FLOOR - REQUEST BROKER SL | " +
                                $"peak={peakProfit:F2} | profit={currentProfit:F2} | " +
                                $"targetUsd={floorUsd:F2} | requestedSL={requestedSL:F5} | " +
                                $"validatedSL={goldProtectedValidSL:F5} | fullFloorPossible={meetsRequestedFloor}";
                            LogMoveSL(trade, stage, goldProtectedValidSL, reason);
                            return RiskDecision.MoveSL(stage, goldProtectedValidSL, reason);
                        }

                        if (!stillInProfit || !meetsRequestedFloor)
                            LogFile.Write($"[{trade.Symbol}] GOLD PROFIT FLOOR SL CONSTRAINED | " +
                                $"peak={peakProfit:F2} | targetUsd={floorUsd:F2} | " +
                                $"requestedSL={requestedSL:F5} | goldProtectedValidSL={goldProtectedValidSL:F5} | " +
                                $"currentProfit={currentProfit:F2} | minimumDistance={minimumStopDistance:F5}");

                        return RiskDecision.Hold(stage,
                            "Gold profit floor active; retain existing better SL or await broker distance");
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
                    // CASH-only: a trade that previously qualified as a runner
                    // must not begin a NEW trailing-stop modification after
                    // its entire unrealized profit has disappeared.
                    // Request a software close instead; a broker-side stop
                    // remains the fallback if the close cannot be filled.
                    if (IsCashIndex(s) &&
                        peakProfit >= profile.TrailAtProfitUsd &&
                        currentProfit <= 0.0)
                    {
                        string reason =
                            $"CASH runner profit fully reversed | " +
                            $"peak={peakProfit:F2} | profit={currentProfit:F2} | " +
                            $"giveback={giveback:F2} | " +
                            $"trailArm={profile.TrailAtProfitUsd:F2}";
                        LogClose(trade, stage, reason);
                        return RiskDecision.Close(stage, reason);
                    }

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


                            // Preserve the user's preference for broker-side stop movement.
                            // Do not software-close on peak-profit giveback alone.
                            // Existing structural / hard-loss exits remain active.

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


                        // Preserve the same GOLD peak-profit floor above $18.
                        // Healthy runner trailing cannot silently bypass it.
                        if (tickSize > 0 && tickValue > 0 && trade.Volume > 0)
                        {
                            double runnerFloorSL = ProfitUsdToStopPrice(
                                trade, GoldProfitFloorUsd(peakProfit), tickSize, tickValue);
                            goldProposedSL = trade.Side == TradeSide.Buy
                                ? Math.Max(goldProposedSL, runnerFloorSL)
                                : Math.Min(goldProposedSL, runnerFloorSL);
                        }
                        else
                        {
                            LogFile.Write($"[{trade.Symbol}] GOLD RUNNER FLOOR SL NOT CALCULABLE | " +
                                $"peak={peakProfit:F2} | profit={currentProfit:F2} | " +
                                "software floor remains active; verify broker SL");
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
                // $9:
                // Peak-dollar protection activates.
                //
                // $15:
                // ATR runner activates.
                // ========================================================

                if (IsGold(s))
                {
                    return new Profile
                    {
                        // ----------------------------------------------------
                        // GOLD VOLATILITY-AWARE HARD LOSS
                        //
                        // Request emergency software close at -$6, independent
                        // of indicator readiness or ATR. Configured upper
                        // ceiling is $7, NOT a guaranteed realized-loss cap.
                        // Existing ATR parameter is kept for compatibility.
                        // Broker-side entry SL must be set and verified by
                        // the order executor for protection during downtime.
                        // ----------------------------------------------------
                        HardLossUsd = 6.00,
                        HardLossAtrFloor = 0.55,
                        MaxHardLossUsd = 7.00,

                        StructuralAdverseAtr = 0.65,

                        Ema21FailureAtr = 0.20,

                        OppositeBodyRatio = 0.62,


                        // Gold broker-side peak-profit floor begins at $8.
                        // At $13 peak, target at least $6 profit.
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


                        MinStopDistanceAtr = 0.10,

                        // Retained for profile compatibility, but Gold's
                        // early broker-side profit stop is intentionally bypassed.
                        // Gold must first prove itself at ProtectAtProfitUsd.
                        EarlyProtectAtProfitUsd = 3.00,
                        EarlyLockPercent = 0.35,

                        EarlyBrokerMinDistanceAtr = 0.35,
                        EnableEarlySoftwareClose = false,

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