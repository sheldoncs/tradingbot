using System;

namespace MetaTrader.Util
{
    public static class MarketStateEngine
    {
        public static bool IsEmaCompressed(
            double ema9,
            double ema21,
            double slope9,
            double slope21,
            double atr)
        {
            double emaDistance = Math.Abs(ema9 - ema21);

            bool closeTogether = emaDistance < atr * 0.30;
            bool flatSlopes =
                Math.Abs(slope9) < atr * 0.10 &&
                Math.Abs(slope21) < atr * 0.10;

            return closeTogether && flatSlopes;
        }
    }

}
