namespace MetaTrader.Util
{
    public static class MomentumExitEngine
    {
        public static bool ShouldExit(double momentumScore, double prevMomentumScore)
        {
            // Sudden collapse in momentum
            if (momentumScore < prevMomentumScore * 0.6)
                return true;

            // Extremely weak score
            if (momentumScore < 30)
                return true;

            return false;
        }
    }

}
