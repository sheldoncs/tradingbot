using MetaTrader.Enums;

namespace MetaTrader.Util
{
    public static class ExpansionFailureEngine
    {
        /// <summary>
        /// Determines whether an expansion attempt has FAILED
        /// and should be rejected instead of treated as a pullback.
        /// </summary>
        public static bool IsFailedExpansion(
        MarketState state,
        double[] emaFast,
        double[] emaSlow)
        {
            if (state != MarketState.Impulse &&
                state != MarketState.Continuation)
                return false;

            // True trend invalidation
            bool emaBroken =
                emaFast[^1] < emaSlow[^1] &&
                emaFast[^2] > emaSlow[^2];

            return emaBroken;
        }


    }

}
