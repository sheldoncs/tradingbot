namespace MetaTrader.Util
{
    using MetaTrader.Services;
    using MtApi5;
    using System;

   
        public static class SymbolQualityEngine
        {
            /// <summary>
            /// Calculates a quality score for a symbol using full internal analysis.
            /// </summary>
            public static double CalculateScore(
                string symbol,
                MqlRates[] rates1H,
                MqlRates[] rates4H)
            {
                // ----------------------------------------------------------
                // ATR (H1)
                // ----------------------------------------------------------
                double atrRaw = IndicatorEngine.CalculateATR(rates1H, 14);

                // ----------------------------------------------------------
                // EMA calculations
                // ----------------------------------------------------------
                double[] emaFast1H = IndicatorEngine.CalculateEMA(rates1H, 9);
                double[] emaSlow1H = IndicatorEngine.CalculateEMA(rates1H, 21);

                double[] emaFast4H = IndicatorEngine.CalculateEMA(rates4H, 9);
                double[] emaSlow4H = IndicatorEngine.CalculateEMA(rates4H, 21);

                // ----------------------------------------------------------
                // Trend (H1)
                // ----------------------------------------------------------
                var trend = TrendEngine.Analyze(
                    emaFast1H,
                    emaSlow1H,
                    rates1H,
                    atrRaw
                );

                // ----------------------------------------------------------
                // Momentum (H1)
                // ----------------------------------------------------------
                var momentum = HybridMomentumEngine.Calculate(
                    rates1H[^1],
                    rates1H[^2],
                    rates1H,
                    emaFast1H,
                    emaSlow1H,
                    atrRaw, false
                );

                // ----------------------------------------------------------
                // EMA Compression
                // ----------------------------------------------------------
                double ema9 = emaFast1H[^1];
                double ema21 = emaSlow1H[^1];

                double slope9 = emaFast1H[^1] - emaFast1H[^2];
                double slope21 = emaSlow1H[^1] - emaSlow1H[^2];

                bool emaCompressed = MarketStateEngine.IsEmaCompressed(
                    ema9,
                    ema21,
                    slope9,
                    slope21,
                    atrRaw
                );

                // ----------------------------------------------------------
                // H4 Weakness
                // ----------------------------------------------------------
                int i1 = emaFast4H.Length - 1;
                int i0 = emaFast4H.Length - 2;

                double h4Momentum = emaFast4H[i1] - emaSlow4H[i1];
                double h4PrevMomentum = emaFast4H[i0] - emaSlow4H[i0];
                double h4Change = h4Momentum - h4PrevMomentum;

                bool h4Weak =
                    Math.Abs(h4Momentum) < atrRaw * 0.20 ||
                    Math.Abs(h4Change) < atrRaw * 0.03;

                // ----------------------------------------------------------
                // Range fatigue
                // ----------------------------------------------------------
                bool rangeFatigued = RangeFatigueEngine.IsRangeFatigued(
                    rates1H,
                    emaCompressed,
                    momentum,
                    false,
                    false,
                    atrRaw
                );

                // ----------------------------------------------------------
                // Scoring
                // ----------------------------------------------------------
                double score = 0.0;

                if (trend.Direction != TrendDirection.None)
                    score += 2.0;

                if (momentum.Strong)
                    score += 2.0;

                if (momentum.Breakout || momentum.Impulse)
                    score += 1.5;

                if (emaCompressed)
                    score -= 2.0;

                if (h4Weak)
                    score -= 1.5;

                if (rangeFatigued)
                    score -= 2.5;

                return score;
            }
        }
    

}
