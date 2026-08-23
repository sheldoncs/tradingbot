namespace MetaTrader.Services.Helper
{
    using global::MetaTrader.Data.Entities;
    using global::MetaTrader.Util;
    using MtApi5;
    using System;

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

            // Profit protection stage.
            public double ProtectAtProfitUsd;

            public double ProfitLockAtr;

            // Runner / trailing stage.
            public double TrailAtProfitUsd;

            public double TrailDistanceAtr;

            // Minimum SL distance from current market.
            public double MinStopDistanceAtr;
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
            double brokerMinStopDistancePrice = 0)
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
            // Absolute per-trade emergency stop.
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
            // 4. CURRENT EXECUTABLE LIVE PRICE
            //
            // BUY position exits at BID.
            // SELL position exits at ASK.
            // ========================================================

            double currentPrice =
                trade.Side == TradeSide.Buy
                    ? bid
                    : ask;


            // ========================================================
            // 5. ADVERSE MOVE FROM ENTRY
            //
            // THIS ALREADY USES LIVE BID / ASK.
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
            // 6. CLOSED CANDLE EVIDENCE
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
            // 7. LIVE ADVERSE CANDLE EVIDENCE
            //
            // Uses executable BID/ASK rather than waiting for candle
            // close.
            //
            // BUY  -> BID below candle open = bearish live candle.
            // SELL -> ASK above candle open = bullish live candle.
            //
            // High/low are expanded to include live market price.
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
            // 8. COMBINED ADVERSE CANDLE EVIDENCE
            //
            // Either:
            //
            // 1. completed opposite candle
            //
            // OR
            //
            // 2. live opposite candle using BID / ASK
            //
            // can provide candle evidence.
            //
            // This alone CANNOT close a trade.
            // ========================================================

            bool adverseCandleEvidence =
                closedOppositeStrongCandle ||
                liveOppositeStrongCandle;


            // ========================================================
            // 9. EMA VALUES
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
            // 10. EMA21 FAILURE DISTANCE
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
            // 11. TRUE STRUCTURAL FAILURE
            //
            // STILL REQUIRES ALL THREE:
            //
            // 1. meaningful adverse movement
            // 2. decisive EMA21 failure
            // 3. adverse candle evidence
            //
            // Candle evidence may now come from:
            //
            // - closed candle
            // OR
            // - live BID/ASK candle
            //
            // A live candle alone DOES NOT close the trade.
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

                    $"ema21FailureATR={ema21FailureAtr:F2}/" +
                    $"{profile.Ema21FailureAtr:F2} | " +

                    $"closedBody={closedBodyRatio:F2} | " +

                    $"liveBody={liveBodyRatio:F2} | " +

                    $"requiredBody={profile.OppositeBodyRatio:F2} | " +

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
            // 12. INITIAL / DEVELOPING
            //
            // Let trades breathe.
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
            // 13. MINIMUM VALID STOP DISTANCE
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
            // 14. PROTECTED STAGE
            //
            // Move SL into small profit.
            // Do NOT close simply because profit retraced.
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


                double validSL =
                    ValidateStopLoss(
                        trade,
                        proposedSL,
                        bid,
                        ask,
                        minimumStopDistance
                    );


                if (ShouldImproveStop(
                        trade,
                        validSL))
                {
                    string reason =
                        $"Profit protection | " +
                        $"peak={peakProfit:F2} | " +
                        $"profit={currentProfit:F2} | " +
                        $"giveback={giveback:F2}";

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
                    "Protected - existing SL sufficient"
                );


                return RiskDecision.Hold(
                    stage,
                    "Protected"
                );
            }


            // ========================================================
            // 15. TRAILING STAGE
            //
            // Winner is protected through SL movement rather than
            // repeatedly issuing CLOSE.
            // ========================================================

            if (stage ==
                RiskStage.Trailing)
            {
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
                // Minimum protected-profit floor
                // ====================================================

                double profitFloor =
                    trade.Side == TradeSide.Buy

                        ? trade.EntryPrice +
                          atr *
                          profile.ProfitLockAtr

                        : trade.EntryPrice -
                          atr *
                          profile.ProfitLockAtr;


                double proposedSL;


                if (trade.Side == TradeSide.Buy)
                {
                    proposedSL =
                        Math.Max(
                            trailingSL,
                            profitFloor
                        );
                }
                else
                {
                    proposedSL =
                        Math.Min(
                            trailingSL,
                            profitFloor
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


                if (ShouldImproveStop(
                        trade,
                        validSL))
                {
                    string reason =
                        $"ATR runner trail | " +
                        $"peak={peakProfit:F2} | " +
                        $"profit={currentProfit:F2} | " +
                        $"giveback={giveback:F2} | " +
                        $"trailATR={profile.TrailDistanceAtr:F2}";

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
                    "Runner - hold existing trailing stop"
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
        // STOP VALIDATION
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
        // NEVER MOVE SL BACKWARDS
        // ============================================================

        private static bool ShouldImproveStop(
            TrackedTrade trade,
            double newSL)
        {
            if (newSL <= 0)
                return false;


            if (trade.StopLoss == 0)
                return true;


            if (trade.Side ==
                TradeSide.Buy)
            {
                return
                    newSL >
                    trade.StopLoss;
            }


            return
                newSL <
                trade.StopLoss;
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

                    MinStopDistanceAtr = 0.10
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

                    MinStopDistanceAtr = 0.10
                };
            }


            // ========================================================
            // BNB / SOL / XRP / ADA
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
