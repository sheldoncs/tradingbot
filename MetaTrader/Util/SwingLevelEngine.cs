using MtApi5;

namespace MetaTrader.Util
{
    public static class SwingLevelEngine
    {
        public static double GetRecentSwingHigh(MqlRates[] rates, int lookback = 20)
        {
            for (int i = rates.Length - lookback; i < rates.Length - 1; i++)
            {
                if (i <= 0 || i >= rates.Length - 1)
                    continue;

                bool isSwingHigh =
                    rates[i].high > rates[i - 1].high &&
                    rates[i].high > rates[i + 1].high;

                if (isSwingHigh)
                {
                    LogFile.Write($"🧱 Swing-high selected at {rates[i].high:F2}");
                    return rates[i].high;
                }
            }

            return double.NaN; // no valid recent swing
        }


        public static double GetRecentSwingLow(MqlRates[] rates, int lookback = 20)
        {
            for (int i = rates.Length - lookback; i < rates.Length - 1; i++)
            {
                if (i <= 0 || i >= rates.Length - 1)
                    continue;

                bool isSwingLow =
                    rates[i].low < rates[i - 1].low &&
                    rates[i].low < rates[i + 1].low;

                if (isSwingLow)
                {
                    LogFile.Write($"🧱 Swing-low selected at {rates[i].low:F2}");
                    return rates[i].low;
                }
            }

            return double.NaN;
        }

    }

}
