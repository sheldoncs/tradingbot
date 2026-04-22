using MtApi5;

namespace MetaTrader.Util
{
    public static class TrendContinuation
    {
        public static bool IsBullTrendBuilding(MqlRates[] candles, double[] emaFast, double[] emaSlow)
        {
            int i = candles.Length - 1;

            // 1) EMA Alignment
            bool emaAligned =
                emaFast[i] > emaSlow[i] &&
                emaFast[i - 1] > emaSlow[i - 1];

            // 2) Higher Low structure
            bool higherLow = candles[i].low > candles[i - 1].low;

            // 3) Bullish candle close
            bool bullishClose = candles[i].close > candles[i].open;

            return emaAligned && higherLow && bullishClose;
        }

        public static bool IsBearTrendBuilding(MqlRates[] candles, double[] emaFast, double[] emaSlow)
        {
            int i = candles.Length - 1;

            // 1) EMA Alignment
            bool emaAligned =
                emaFast[i] < emaSlow[i] &&
                emaFast[i - 1] < emaSlow[i - 1];

            // 2) Lower High structure
            bool lowerHigh = candles[i].high < candles[i - 1].high;

            // 3) Bearish candle close
            bool bearishClose = candles[i].close < candles[i].open;

            return emaAligned && lowerHigh && bearishClose;
        }
    }

}
