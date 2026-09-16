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
                // Absolute dollar loss per trade.
                public double HardLossUsd;

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
                // 3. HARD DOLLAR LOSS
                //
                // Absolute emergency loss limit.
                // ========================================================

                if (currentProfit <=
                    -profile.HardLossUsd)
                {
                    string reason =
                        $"Hard loss limit | " +
                        $"profit={currentProfit:F2} | " +
                        $"limit=-{profile.HardLossUsd:F2}";


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


                bool peakProtectionArmed =
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

                if (earlyProtectionArmed &&
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


                    if (ShouldImproveStop(
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
                // Dollar peak protection is ALREADY ACTIVE above.
                //
                // This section creates a broker-side profit stop as an
                // additional protection layer.
                // ========================================================

                if (stage ==
                    RiskStage.Protected)
                {
                    double lockDistance =
                        atr *
                        profile.ProfitLockAtr;


                    double proposedSL =
                        trade.Side == TradeSide.Buy

                            ? trade.EntryPrice +
                              lockDistance

                            : trade.EntryPrice -
                              lockDistance;


                    // ====================================================
                    // If dollar protection is available, convert the
                    // protected dollar floor into a real SL price too.
                    //
                    // This gives Protected stage BOTH:
                    //
                    // 1. Software close protection
                    // 2. Broker-side dollar floor where possible
                    // ====================================================

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


                        LogFile.Write(
                            $"[{trade.Symbol}] 💵 Protected dollar SL | " +
                            $"ticket={trade.Ticket} | " +
                            $"peak={peakProfit:F2} | " +
                            $"protectedUsd={protectedProfitUsd:F2} | " +
                            $"volume={trade.Volume:F2} | " +
                            $"tickSize={tickSize:F8} | " +
                            $"tickValue={tickValue:F5} | " +
                            $"rawDollarSL={dollarProfitFloorSL:F5}"
                        );
                    }


                    double validSL =
                        ValidateStopLoss(
                            trade,
                            proposedSL,
                            bid,
                            ask,
                            minimumStopDistance
                        );


                    // ====================================================
                    // NORMALIZE TO REAL BROKER TICK
                    //
                    // BUY:
                    // round DOWN so SL cannot accidentally become too
                    // close to BID.
                    //
                    // SELL:
                    // round UP so SL cannot accidentally become too
                    // close to ASK.
                    // ====================================================

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
                        peakProtectionArmed
                            ? $"Protected - dollar floor=${protectedProfitUsd:F2}"
                            : "Protected - existing SL sufficient"
                    );


                    return RiskDecision.Hold(
                        stage,
                        "Protected"
                    );
                }


                // ========================================================
                // 17. TRAILING STAGE
                //
                // At this stage:
                //
                // 1. Dollar peak protection is already active.
                // 2. ATR runner trailing now becomes active too.
                // ========================================================

                if (stage ==
                    RiskStage.Trailing)
                {
                    // ====================================================
                    // 17A. ATR RUNNER TRAILING STOP
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


                    // ====================================================
                    // 17B. ATR MINIMUM PROFIT FLOOR
                    // ====================================================

                    double atrProfitFloorSL =
                        trade.Side == TradeSide.Buy

                            ? trade.EntryPrice +
                              atr *
                              profile.ProfitLockAtr

                            : trade.EntryPrice -
                              atr *
                              profile.ProfitLockAtr;


                    // ====================================================
                    // 17C. CONVERT PROTECTED USD FLOOR TO PRICE
                    // ====================================================

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


                        LogFile.Write(
                            $"[{trade.Symbol}] 💵 Dollar protection SL | " +
                            $"ticket={trade.Ticket} | " +
                            $"peak={peakProfit:F2} | " +
                            $"protectedUsd={protectedProfitUsd:F2} | " +
                            $"volume={trade.Volume:F2} | " +
                            $"tickSize={tickSize:F8} | " +
                            $"tickValue={tickValue:F5} | " +
                            $"rawDollarSL={dollarProfitFloorSL:F5}"
                        );
                    }


                    // ====================================================
                    // 17D. CHOOSE STRONGEST PROTECTIVE STOP
                    //
                    // BUY:
                    // higher = stronger
                    //
                    // SELL:
                    // lower = stronger
                    // ====================================================

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


                    // ====================================================
                    // 17E. VALIDATE AGAINST CURRENT MARKET
                    // ====================================================

                    double validSL =
                        ValidateStopLoss(
                            trade,
                            proposedSL,
                            bid,
                            ask,
                            minimumStopDistance
                        );


                    // ====================================================
                    // 17F. NORMALIZE TO REAL TICK SIZE
                    // ====================================================

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
                        peakProtectionArmed
                            ? $"Runner - protected dollar floor=${protectedProfitUsd:F2}"
                            : "Runner - ATR trailing only"
                    );


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
                        HardLossUsd = 6.00,

                        StructuralAdverseAtr = 0.65,

                        Ema21FailureAtr = 0.20,

                        OppositeBodyRatio = 0.62,


                        ProtectAtProfitUsd = 9.00,

                        ProfitLockAtr = 0.05,


                        TrailAtProfitUsd = 15.00,

                        TrailDistanceAtr = 0.70,


                        // Keep at least 60% of peak profit.
                        PeakProfitLockPercent = 0.60,


                        // Never surrender more than $7
                        // once protection has armed.
                        MaxGivebackUsd = 7.00,


                        FollowThroughBars = 2,

                        FollowThroughMinPeakProfitUsd = 3.00,

                        FollowThroughEma9FailureAtr = 0.12,


                        MinStopDistanceAtr = 0.10,

                        EarlyProtectAtProfitUsd = 3.00,
                        EarlyLockPercent = 0.35,

                        // Give Gold more room during early profit protection.
                        // The broker-side stop must remain at least 0.35 ATR
                        // from the live market, and ordinary early giveback
                        // must not force an immediate software close.
                        EarlyBrokerMinDistanceAtr = 0.35,
                        EnableEarlySoftwareClose = false
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

                        TrailDistanceAtr = 0.80,

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
                // BNB / SOL / XRP / ADA / OTHER CRYPTO
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
                        EarlyProtectAtProfitUsd = 2.50,
                        EarlyLockPercent = 0.35,

                        EarlyBrokerMinDistanceAtr = 0.30,
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
                    symbol.Contains("UK100");
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