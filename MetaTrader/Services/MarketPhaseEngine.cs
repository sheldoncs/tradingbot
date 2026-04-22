using MetaTrader.Enums;
using MetaTrader.Util;
using System;

namespace MetaTrader.Services
{
    public static class MarketPhaseEngine
    {
        // Tune these once, then leave them stable
        private const int ShockLookbackCandles = 3;          // must match your shock guard lookback
        private const double AtrCoolingPercentile = 0.85;    // <= 85th percentile means "cooling enough"
        private const int MinimumStructuringCandles = 2;     // require at least 2 calm candles

        public static MarketPhaseLoop Evaluate(
            SymbolContext ctx,
            DateTime currentH1CandleTime,
            bool hasOpenTrade,
            bool shockDetectedThisCandle,
            double atrPercentile,
            bool emaCompressed,
            bool m15Confirmed,
            bool isArmed,
            out string reason)
        {
            reason = "";

            // ------------------------------------------------------------
            // 0) ACTIVE overrides everything
            // ------------------------------------------------------------
            if (hasOpenTrade)
            {
                reason = "Active trade open";
                return MarketPhaseLoop.Active;
            }

            // ------------------------------------------------------------
            // 1) Shock detection & tracking (increment candles since shock)
            // ------------------------------------------------------------
            // Only update once per NEW H1 candle
            if (ctx.LastPhaseUpdateCandleTime != currentH1CandleTime)
            {
                ctx.LastPhaseUpdateCandleTime = currentH1CandleTime;

                if (ctx.LastShockCandleTime.HasValue)
                    ctx.CandlesSinceShock++;
            }

            if (shockDetectedThisCandle)
            {
                ctx.LastShockCandleTime = currentH1CandleTime;
                ctx.CandlesSinceShock = 0;
                reason = "Shock detected (range/body vs ATR)";
                return MarketPhaseLoop.Shock;
            }

            // If a shock happened recently, we remain in Shock/Cooling/Structuring until cleared
            bool shockStillInWindow =
                ctx.LastShockCandleTime.HasValue &&
                ctx.CandlesSinceShock < ShockLookbackCandles;

            // ------------------------------------------------------------
            // 2) SHOCK phase (recent shock still inside lookback window)
            // ------------------------------------------------------------
            if (shockStillInWindow)
            {
                reason = $"Recent shock in window ({ctx.CandlesSinceShock}/{ShockLookbackCandles})";
                return MarketPhaseLoop.Shock;
            }

            // ------------------------------------------------------------
            // 3) COOLING phase (shock window passed OR no shock,
            //    but volatility still elevated / structure not ready)
            // ------------------------------------------------------------
            bool atrCooled = atrPercentile <= AtrCoolingPercentile;

            if (!atrCooled)
            {
                reason = $"ATR not cooled (pct={atrPercentile:0.00} > {AtrCoolingPercentile:0.00})";
                return MarketPhaseLoop.Cooling;
            }

            // ------------------------------------------------------------
            // 4) STRUCTURING phase
            //    We want evidence of "market becoming orderly":
            //    - EMA compression OR controlled candles / pullback logic
            //    You already have emaCompressed; you can extend later.
            // ------------------------------------------------------------
            // If not compressed and M15 not confirmed, we call it Structuring (not ready)
            if (!emaCompressed || !m15Confirmed)
            {
                reason = $"Structuring: emaCompressed={emaCompressed}, m15Confirmed={m15Confirmed}";
                return MarketPhaseLoop.Structuring;
            }

            // ------------------------------------------------------------
            // 5) TRADABLE phase
            //    Must be Armed + cooled + structure ok
            // ------------------------------------------------------------
            if (!isArmed)
            {
                reason = "Structure ok but not Armed yet";
                return MarketPhaseLoop.Structuring;
            }

            reason = "Tradable: cooled + structured + armed";
            return MarketPhaseLoop.Tradable;
        }
    }

}
