using System;

namespace MetaTrader.Util
{
    public static class H4OverrideEngine
    {
        /// <summary>
        /// Determines whether the bot should override H4 weakness
        /// based on extremely strong H1 momentum.
        /// </summary>
        public static bool ShouldOverrideH4Weakness(
            double momentumScore,
            double dynamicThreshold,
            double lastBody,
            double atr,
            bool bullishBreakout,
            bool bearishBreakout,
            bool h4Reversal,
            double h4Change,
            double h4Momentum)
        {
            // --------------------------------------------------------
            // MASSIVE MOMENTUM CHECK
            // --------------------------------------------------------
            bool strongMomentum = momentumScore >= dynamicThreshold * 1.8;

            // --------------------------------------------------------
            // LARGE CANDLE BODY (true continuation)
            // --------------------------------------------------------
            bool largeBody = lastBody >= atr * 0.50;

            // --------------------------------------------------------
            // STRUCTURAL BREAKOUT REQUIRED
            // --------------------------------------------------------
            bool breakout = bullishBreakout || bearishBreakout;

            // --------------------------------------------------------
            // H4 NOT REVERSING
            // --------------------------------------------------------
            bool h4NotReversing = !h4Reversal;

            // --------------------------------------------------------
            // H4 must not be collapsing
            // --------------------------------------------------------
            bool h4NotCollapsing = Math.Abs(h4Change) > atr * 0.02;

            // --------------------------------------------------------
            // H4 trend must be weak (not strong opposite)
            // --------------------------------------------------------
            bool h4WeakTrend = Math.Abs(h4Momentum) < atr * 0.25;

            // --------------------------------------------------------
            // FINAL CONDITION
            // --------------------------------------------------------
            bool allowOverride =
                strongMomentum &&
                largeBody &&
                breakout &&
                h4NotReversing &&
                h4NotCollapsing &&
                h4WeakTrend;

            if (allowOverride)
            {
                Console.WriteLine("⚡ H4 override triggered → Momentum extremely strong");
                LogFile.Write("⚡ H4 override triggered → Momentum extremely strong");
            }
            else
            {
                Console.WriteLine("H4 override NOT triggered → Conditions not met");
                LogFile.Write("H4 override NOT triggered → Conditions not met");
            }

            return allowOverride;
        }
    }

}
