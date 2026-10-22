using MetaTrader.Enums;
using MetaTrader.Util;
using System;

namespace MetaTrader.Services.Helper
{
    public static class FollowThroughEntryEngine
    {
        // ================================================================
        // FOLLOW-THROUGH ENTRY ENGINE
        // ================================================================
        //
        // PURPOSE:
        //
        // Protect LiveImpulse entries from entering after momentum has
        // materially deteriorated and price is already extended.
        //
        // IMPORTANT:
        //
        // Momentum rising/falling by itself is NOT enough to block.
        //
        // We require a combination of:
        //
        // 1. Momentum moving against the proposed trade
        // 2. Momentum deterioration is significant
        // 3. Current momentum is not strong
        // 4. Price is already extended from EMA9
        // 5. There is no confirmed breakout
        //
        // This prevents the filter from rejecting healthy trades simply
        // because momentum temporarily dipped.
        // ================================================================



        public static bool AllowEntry(
            string symbol,
            TradeAction action,
            double momentum,
            double previousMomentum,
            bool momentumRising,
            bool momentumFalling,
            bool strongMomentum,
            double emaDistanceAtr,
            double bodyAtr,
            bool confirmedBreakout)
        {
            try
            {
                // ========================================================
                // ONLY CHECK ENTRY SIGNALS
                // ========================================================

                if (action != TradeAction.EnterBuy &&
                    action != TradeAction.EnterSell)
                {
                    return true;
                }


                // ========================================================
                // MOMENTUM DETERIORATION
                // ========================================================

                double momentumChange =
                    momentum -
                    previousMomentum;


                double momentumDrop =
                    Math.Max(
                        0.0,
                        previousMomentum - momentum
                    );


                double momentumIncrease =
                    Math.Max(
                        0.0,
                        momentum - previousMomentum
                    );


                double momentumDropPercent = 0.0;

                if (previousMomentum > 0.000001)
                {
                    momentumDropPercent =
                        momentumDrop /
                        previousMomentum;
                }


                // ========================================================
                // QUALITY THRESHOLDS
                // ========================================================
                //
                // These are deliberately conservative.
                //
                // We do NOT want the engine blocking normal momentum
                // breathing/pullbacks.
                // ========================================================

                const double WEAK_MOMENTUM_LEVEL = 0.50;

                const double SIGNIFICANT_DROP = 0.20;

                const double SIGNIFICANT_DROP_PERCENT = 0.30;

                const double EXTENDED_EMA_DISTANCE = 1.35;

                const double STRONG_BODY_ATR = 1.00;


                // ========================================================
                // BUY FOLLOW-THROUGH
                // ========================================================

                if (action == TradeAction.EnterBuy)
                {
                    bool momentumAgainstTrade =
                        momentumFalling;


                    bool significantDeterioration =
                        momentumDrop >= SIGNIFICANT_DROP ||
                        momentumDropPercent >= SIGNIFICANT_DROP_PERCENT;


                    bool weakCurrentMomentum =
                        momentum < WEAK_MOMENTUM_LEVEL;


                    bool priceExtended =
                        emaDistanceAtr >= EXTENDED_EMA_DISTANCE;


                    bool bodyStillPowerful =
                        bodyAtr >= STRONG_BODY_ATR;


                    // ----------------------------------------------------
                    // HARD BLOCK
                    // ----------------------------------------------------
                    //
                    // Only block when several weaknesses exist together.
                    //
                    // ----------------------------------------------------

                    bool blockBuy =
                        momentumAgainstTrade &&
                        significantDeterioration &&
                        weakCurrentMomentum &&
                        !strongMomentum &&
                        priceExtended &&
                        !bodyStillPowerful &&
                        !confirmedBreakout;


                    if (blockBuy)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ FOLLOW-THROUGH ENGINE BLOCKED BUY | " +
                            $"momentum={momentum:F2} | " +
                            $"previousMomentum={previousMomentum:F2} | " +
                            $"change={momentumChange:F2} | " +
                            $"drop={momentumDrop:F2} | " +
                            $"dropPct={momentumDropPercent:P0} | " +
                            $"rising={momentumRising} | " +
                            $"falling={momentumFalling} | " +
                            $"strong={strongMomentum} | " +
                            $"emaDistATR={emaDistanceAtr:F2} | " +
                            $"bodyATR={bodyAtr:F2} | " +
                            $"confirmedBreakout={confirmedBreakout} | " +
                            $"reason=BUY momentum materially deteriorated while price is already extended"
                        );


                        return false;
                    }


                    LogFile.Write(
                        $"[{symbol}] ✅ FOLLOW-THROUGH ENGINE BUY ALLOWED | " +
                        $"momentum={momentum:F2} | " +
                        $"previousMomentum={previousMomentum:F2} | " +
                        $"change={momentumChange:F2} | " +
                        $"drop={momentumDrop:F2} | " +
                        $"dropPct={momentumDropPercent:P0} | " +
                        $"rising={momentumRising} | " +
                        $"falling={momentumFalling} | " +
                        $"strong={strongMomentum} | " +
                        $"emaDistATR={emaDistanceAtr:F2} | " +
                        $"bodyATR={bodyAtr:F2} | " +
                        $"confirmedBreakout={confirmedBreakout}"
                    );


                    return true;
                }


                // ========================================================
                // SELL FOLLOW-THROUGH
                // ========================================================

                if (action == TradeAction.EnterSell)
                {
                    bool momentumAgainstTrade =
                        momentumRising;


                    // For SELL, rising momentum represents deterioration
                    // of the bearish impulse.

                    double deteriorationPercent = 0.0;

                    if (previousMomentum > 0.000001)
                    {
                        deteriorationPercent =
                            momentumIncrease /
                            previousMomentum;
                    }


                    bool significantDeterioration =
                        momentumIncrease >= SIGNIFICANT_DROP ||
                        deteriorationPercent >= SIGNIFICANT_DROP_PERCENT;


                    bool weakCurrentMomentum =
                        momentum < WEAK_MOMENTUM_LEVEL;


                    bool priceExtended =
                        emaDistanceAtr >= EXTENDED_EMA_DISTANCE;


                    bool bodyStillPowerful =
                        bodyAtr >= STRONG_BODY_ATR;


                    bool blockSell =
                        momentumAgainstTrade &&
                        significantDeterioration &&
                        weakCurrentMomentum &&
                        !strongMomentum &&
                        priceExtended &&
                        !bodyStillPowerful &&
                        !confirmedBreakout;


                    if (blockSell)
                    {
                        LogFile.Write(
                            $"[{symbol}] ⛔ FOLLOW-THROUGH ENGINE BLOCKED SELL | " +
                            $"momentum={momentum:F2} | " +
                            $"previousMomentum={previousMomentum:F2} | " +
                            $"change={momentumChange:F2} | " +
                            $"rise={momentumIncrease:F2} | " +
                            $"risePct={deteriorationPercent:P0} | " +
                            $"rising={momentumRising} | " +
                            $"falling={momentumFalling} | " +
                            $"strong={strongMomentum} | " +
                            $"emaDistATR={emaDistanceAtr:F2} | " +
                            $"bodyATR={bodyAtr:F2} | " +
                            $"confirmedBreakout={confirmedBreakout} | " +
                            $"reason=SELL momentum materially deteriorated while price is already extended"
                        );


                        return false;
                    }


                    LogFile.Write(
                        $"[{symbol}] ✅ FOLLOW-THROUGH ENGINE SELL ALLOWED | " +
                        $"momentum={momentum:F2} | " +
                        $"previousMomentum={previousMomentum:F2} | " +
                        $"change={momentumChange:F2} | " +
                        $"rise={momentumIncrease:F2} | " +
                        $"risePct={deteriorationPercent:P0} | " +
                        $"rising={momentumRising} | " +
                        $"falling={momentumFalling} | " +
                        $"strong={strongMomentum} | " +
                        $"emaDistATR={emaDistanceAtr:F2} | " +
                        $"bodyATR={bodyAtr:F2} | " +
                        $"confirmedBreakout={confirmedBreakout}"
                    );


                    return true;
                }


                return true;
            }
            catch (Exception ex)
            {
                LogFile.Write(
                    $"[{symbol}] FollowThroughEntryEngine error | " +
                    $"message={ex.Message}"
                );


                // Fail safely:
                // do not open a new trade if this filter cannot evaluate.
                return false;
            }
        }
    }
}
