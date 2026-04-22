using static MetaTrader.Services.HybridMomentumEngine;

namespace MetaTrader.Util
{
    public static class SymbolScoreEngine
    {
        public static double ScoreSymbol(
            string symbol,
            TrendContext trend,
            MomentumResult momentum,
            bool emaCompressed,
            bool h4Weak,
            bool rangeFatigued)
        {
            double score = 0;

            // Trend alignment
            if (trend.Direction != TrendDirection.None)
                score += 2.0;

            // Strong momentum
            if (momentum.Strong)
                score += 2.0;

            // Breakout or impulse
            if (momentum.Breakout || momentum.Impulse)
                score += 1.5;

            // Penalize bad regimes
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
