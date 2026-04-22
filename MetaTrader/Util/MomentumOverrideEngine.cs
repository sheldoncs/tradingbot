using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class MomentumOverrideEngine
    {
        // -------------------------------------------------------------
        // 🔓 BUY-SIDE MOMENTUM OVERRIDE
        // -------------------------------------------------------------
        public static bool AllowBuyOverride(
            MqlRates last,          // last closed H1 candle
            double emaFast,         // EMA 9 (or fast EMA)
            double emaSlow,         // EMA 21 (or slow EMA)
            double recentHigh,      // swing high / resistance
            double atrPoints,       // ATR in points
            double buffer)          // structure buffer
        {
            return CheckMomentum(
                isBullish: true,
                last,
                emaFast,
                emaSlow,
                recentHigh,
                atrPoints,
                buffer);
        }

        // -------------------------------------------------------------
        // 🔓 SELL-SIDE MOMENTUM OVERRIDE
        // -------------------------------------------------------------
        public static bool AllowSellOverride(
            MqlRates last,          // last closed H1 candle
            double emaFast,         // EMA 9 (or fast EMA)
            double emaSlow,         // EMA 21 (or slow EMA)
            double recentLow,       // swing low / support
            double atrPoints,       // ATR in points
            double buffer)          // structure buffer
        {
            return CheckMomentum(
                isBullish: false,
                last,
                emaFast,
                emaSlow,
                recentLow,
                atrPoints,
                buffer);
        }

        // -------------------------------------------------------------
        // 🧠 CORE MOMENTUM + ACCEPTANCE LOGIC
        // -------------------------------------------------------------
        private static bool CheckMomentum(
            bool isBullish,
            MqlRates last,
            double emaFast,
            double emaSlow,
            double level,           // recentHigh or recentLow
            double atrPoints,
            double buffer)
        {
            // ---------------------------------------------------------
            // 1️⃣ Strong momentum candle (body-based, not wick-based)
            // ---------------------------------------------------------
            double body = Math.Abs(last.close - last.open);

            bool strongMomentum =
                body > atrPoints * 0.7 &&
                (isBullish
                    ? last.close > last.open
                    : last.close < last.open);

            if (!strongMomentum)
                return false;

            // ---------------------------------------------------------
            // 2️⃣ Acceptance beyond structure (CLOSE, not wick)
            // ---------------------------------------------------------
            bool acceptedBeyondLevel =
                isBullish
                    ? last.close > level + buffer * 0.5
                    : last.close < level - buffer * 0.5;

            if (!acceptedBeyondLevel)
                return false;

            // ---------------------------------------------------------
            // 3️⃣ EMA expansion confirms follow-through
            // ---------------------------------------------------------
            bool emaExpansion =
                isBullish
                    ? (emaFast - emaSlow) > atrPoints * 0.15
                    : (emaSlow - emaFast) > atrPoints * 0.15;

            if (!emaExpansion)
                return false;

            // ---------------------------------------------------------
            // ✅ ALL CONDITIONS PASSED → OVERRIDE ALLOWED
            // ---------------------------------------------------------
            return true;
        }
    }

}
