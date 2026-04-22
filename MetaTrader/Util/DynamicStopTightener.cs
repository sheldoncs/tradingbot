namespace MetaTrader.Util
{
    public static class DynamicStopTightener
    {
        public static double GetDynamicStop(
            double entryPrice,
            double atr,
            double momentumScore,
            bool isBuy)
        {
            // Base ATR multiplier
            double baseMultiplier = 1.0;

            // Expand stop during strong momentum
            if (momentumScore > 70)
                baseMultiplier = 1.5;

            // Tighten stop when momentum weakens
            if (momentumScore < 40)
                baseMultiplier = 0.6;

            double stopDistance = atr * baseMultiplier;

            return isBuy
                ? entryPrice - stopDistance
                : entryPrice + stopDistance;
        }
    }

}
