using MetaTrader.Data.Entities;
using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class OrderBlockEngine
    {
        public static OrderBlock DetectImpulseOrderBlock(
            MqlRates[] rates,
            bool isBullish)
        {
            // Look back 3–5 candles for last opposite-close candle
            for (int i = rates.Length - 3; i >= rates.Length - 8; i--)
            {
                var c = rates[i];

                if (isBullish && c.close < c.open)
                    return new OrderBlock
                    {
                        High = c.high,
                        Low = c.low,
                        IsBullish = true,
                        Time = c.time
                    };

                if (!isBullish && c.close > c.open)
                    return new OrderBlock
                    {
                        High = c.high,
                        Low = c.low,
                        IsBullish = false,
                        Time = c.time
                    };
            }

            return null;
        }
    }



}
