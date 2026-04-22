using MtApi5;

namespace MetaTrader.Util
{
    public static class SwingDetector
    {
        public static double? GetLastSwingHigh(MqlRates[] rates, int lookback = 20)
        {
            for (int i = rates.Length - 3; i >= rates.Length - lookback; i--)
            {
                if (i <= 0) break;

                if (rates[i].high > rates[i - 1].high &&
                    rates[i].high > rates[i + 1].high)
                    return rates[i].high;
            }
            return null;
        }

        public static double? GetLastSwingLow(MqlRates[] rates, int lookback = 20)
        {
            for (int i = rates.Length - 3; i >= rates.Length - lookback; i--)
            {
                if (i <= 0) break;

                if (rates[i].low < rates[i - 1].low &&
                    rates[i].low < rates[i + 1].low)
                    return rates[i].low;
            }
            return null;
        }
    }

}
