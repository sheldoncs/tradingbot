using MtApi5;
using System;

namespace MetaTrader.Util
{
    public enum BreakdownType
    {
        None,
        LiquiditySweep,
        RealBreakdown
    }

    public static class LiquidityBreakdownEngine
    {
        // -------------------------------------------------------------
        // 🔽 SELL-SIDE ANALYSIS (support breaks)
        // -------------------------------------------------------------
        public static BreakdownType EvaluateSellSide(
            MqlRates last,
            MqlRates prev,
            double recentLow,
            double atrPoints,
            double buffer,
            double emaFast,
            double emaSlow)
        {
            // Candle anatomy
            double body = Math.Abs(last.close - last.open);
            double range = last.high - last.low;
            if (range <= 0) return BreakdownType.None;

            double lowerWick = Math.Min(last.close, last.open) - last.low;
            double bodyRatio = body / range;
            double wickRatio = lowerWick / range;

            // ---------------------------------------------------------
            // 🪤 LIQUIDITY SWEEP (stop-hunt below support)
            // ---------------------------------------------------------
            bool liquiditySweep =
                last.low < recentLow &&                 // swept below support
                last.close >= recentLow &&              // failed to hold below
                wickRatio > 0.45 &&                     // wick-dominant
                bodyRatio < 0.35;                       // weak displacement

            if (liquiditySweep)
                return BreakdownType.LiquiditySweep;

            // ---------------------------------------------------------
            // 🔽 REAL BREAKDOWN (acceptance + momentum)
            // ---------------------------------------------------------
            bool strongBreak =
                last.close < recentLow - buffer &&      // acceptance below
                body > atrPoints * 0.7 &&               // displacement
                wickRatio < 0.35;                       // body-driven

            bool acceptance =
                last.close < recentLow &&
                prev.close < recentLow;

            bool emaExpansionDown =
                (emaSlow - emaFast) > atrPoints * 0.15;

            if (strongBreak && acceptance && emaExpansionDown)
                return BreakdownType.RealBreakdown;

            return BreakdownType.None;
        }

        // -------------------------------------------------------------
        // 🔼 BUY-SIDE ANALYSIS (resistance breaks)
        // -------------------------------------------------------------
        public static BreakdownType EvaluateBuySide(
            MqlRates last,
            MqlRates prev,
            double recentHigh,
            double atrPoints,
            double buffer,
            double emaFast,
            double emaSlow)
        {
            double body = Math.Abs(last.close - last.open);
            double range = last.high - last.low;
            if (range <= 0) return BreakdownType.None;

            double upperWick = last.high - Math.Max(last.close, last.open);
            double bodyRatio = body / range;
            double wickRatio = upperWick / range;

            // ---------------------------------------------------------
            // 🪤 LIQUIDITY SWEEP (false breakout above resistance)
            // ---------------------------------------------------------
            bool liquiditySweep =
                last.high > recentHigh &&               // swept above resistance
                last.close <= recentHigh &&             // failed to hold above
                wickRatio > 0.45 &&
                bodyRatio < 0.35;

            if (liquiditySweep)
                return BreakdownType.LiquiditySweep;

            // ---------------------------------------------------------
            // 🔼 REAL BREAKOUT (acceptance + momentum)
            // ---------------------------------------------------------
            bool strongBreak =
                last.close > recentHigh + buffer &&
                body > atrPoints * 0.7 &&
                wickRatio < 0.35;

            bool acceptance =
                last.close > recentHigh &&
                prev.close > recentHigh;

            bool emaExpansionUp =
                (emaFast - emaSlow) > atrPoints * 0.15;

            if (strongBreak && acceptance && emaExpansionUp)
                return BreakdownType.RealBreakdown;

            return BreakdownType.None;
        }
    }

}
