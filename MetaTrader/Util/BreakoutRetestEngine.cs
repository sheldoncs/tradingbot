using MtApi5;

namespace MetaTrader.Util
{
    public static class BreakoutRetestEngine
    {
        public static bool IsValidRetestBuy(MqlRates last, MqlRates prev)
        {
            // Breakout happened previously
            bool breakoutOccurred = prev.close > prev.high;

            // Retest zone
            bool retest = last.low <= prev.high;

            // Strong continuation candle
            bool continuation = last.close > last.open;

            return breakoutOccurred && retest && continuation;
        }

        public static bool IsValidRetestSell(MqlRates last, MqlRates prev)
        {
            bool breakoutOccurred = prev.close < prev.low;
            bool retest = last.high >= prev.low;
            bool continuation = last.close < last.open;

            return breakoutOccurred && retest && continuation;
        }
    }

}
