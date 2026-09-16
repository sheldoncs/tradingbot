using MtApi5;
using System;
using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Util
{
    public enum MarketRegime
    {
        Expansion,
        Transition,
        Compression,
        Exhaustion,
        Range,
        BreakoutAttempt,
        BullishBreakoutTest,
        BearishBreakoutTest,
        SupportResistanceClear,
        NoQualifyingSetupFound,
        Observation,
        Extended
    }

    public static class MarketRegimeEngine
    {
        public static MarketRegime Detect(
            MqlRates[] rates1H,
            double atrRaw,
            double[] emaFast1H,
            double[] emaSlow1H,
            MomentumResult momentum)
        {
            if (rates1H == null || rates1H.Length < 20)
                return MarketRegime.Compression;

            var last = rates1H[^1];
            var prev = rates1H[^2];

            // -----------------------------
            // 1. Candle expansion vs overlap
            // -----------------------------
            double lastRange = last.high - last.low;
            double prevRange = prev.high - prev.low;

            bool expandingCandle =
                lastRange > atrRaw * 0.8 &&
                Math.Abs(last.close - last.open) / lastRange > 0.55;

            bool overlapping =
                last.high <= prev.high &&
                last.low >= prev.low;

            // -----------------------------
            // 2. EMA slope strength
            // -----------------------------
            double slopeFast = emaFast1H[^1] - emaFast1H[^3];
            double slopeSlow = emaSlow1H[^1] - emaSlow1H[^3];

            bool strongSlope =
                Math.Abs(slopeFast) > atrRaw * 0.05 &&
                Math.Abs(slopeSlow) > atrRaw * 0.03;

            bool flatSlope =
                Math.Abs(slopeFast) < atrRaw * 0.02 &&
                Math.Abs(slopeSlow) < atrRaw * 0.02;

            // -----------------------------
            // 3. Momentum quality
            // -----------------------------
            bool strongMomentum = momentum.Strong && momentum.Hybrid >= 1.2;
            bool weakeningMomentum = momentum.Hybrid < 0.9;

            // -----------------------------
            // REGIME DECISION
            // -----------------------------

            // 🚀 EXPANSION: pay momentum trades
            if (expandingCandle && strongSlope && strongMomentum && !overlapping)
                return MarketRegime.Expansion;

            // ⚠️ TRANSITION: dangerous, fake breaks
            if (strongMomentum && (overlapping || !strongSlope))
                return MarketRegime.Transition;

            // 🧊 COMPRESSION: chop / mean reversion
            if (flatSlope || overlapping)
                return MarketRegime.Compression;

            // 🪫 EXHAUSTION: late trend, fading
            if (weakeningMomentum && strongSlope)
                return MarketRegime.Exhaustion;

            return MarketRegime.Transition;
        }
    }

}
