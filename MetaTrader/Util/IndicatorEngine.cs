namespace MetaTrader.Util
{
    using MtApi5;
    using System;

   
public static class IndicatorEngine
{
    // ----------------------------------------------------------
    // 📊 ATR (Average True Range)
    // ----------------------------------------------------------
    public static double CalculateATR(MqlRates[] rates, int period)
    {
        if (rates == null || rates.Length < period + 1)
            return 0;

        double atr = 0;

        for (int i = rates.Length - period; i < rates.Length; i++)
        {
            double high = rates[i].high;
            double low = rates[i].low;
            double prevClose = rates[i - 1].close;

            double tr = Math.Max(
                high - low,
                Math.Max(
                    Math.Abs(high - prevClose),
                    Math.Abs(low - prevClose)
                )
            );

            atr += tr;
        }

        return atr / period;
    }

    // ----------------------------------------------------------
    // 📈 EMA (Exponential Moving Average)
    // ----------------------------------------------------------
    public static double[] CalculateEMA(MqlRates[] rates, int period)
    {
        if (rates == null || rates.Length < period)
            return Array.Empty<double>();

        double[] ema = new double[rates.Length];

        // --- Seed EMA with SMA ---
        double sum = 0;
        for (int i = 0; i < period; i++)
            sum += rates[i].close;

        double sma = sum / period;
        ema[period - 1] = sma;

        double multiplier = 2.0 / (period + 1);

        // --- EMA calculation ---
        for (int i = period; i < rates.Length; i++)
        {
            ema[i] = ((rates[i].close - ema[i - 1]) * multiplier) + ema[i - 1];
        }

        // Fill early values for safety
        for (int i = 0; i < period - 1; i++)
            ema[i] = ema[period - 1];

        return ema;
    }
}
    

}
