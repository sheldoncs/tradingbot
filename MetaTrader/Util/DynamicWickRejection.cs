using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class DynamicWickRejection
    {
        /// <summary>
        /// Checks for dynamic bullish exhaustion (blocks BUY).
        /// Now includes momentum protection.
        /// </summary>
        public static bool IsBullishExhaustion(
            MqlRates last,
            MqlRates prev,
            double atrRaw)
        {
            double prevBody = Math.Abs(prev.close - prev.open);
            double lastBody = Math.Abs(last.close - last.open);

            // 1️⃣ Strong expansion candle before
            bool prevImpulse = prevBody >= atrRaw * 0.8;

            // 2️⃣ Liquidity sweep above previous high
            bool liquiditySweep = last.high > prev.high;

            // 3️⃣ Failed continuation (did not close above prev high)
            bool failedContinuation = last.close <= prev.high;

            // 4️⃣ Momentum deceleration
            bool deceleration = lastBody < prevBody * 0.5;

            bool condition =
                prevImpulse &&
                liquiditySweep &&
                failedContinuation &&
                deceleration;

            if (condition)
            {
                Console.WriteLine("🔥 Institutional Bullish Exhaustion detected");
                LogFile.Write("🔥 Institutional Bullish Exhaustion detected");
            }

            return condition;
        }

        /// <summary>
        /// Checks for dynamic bearish exhaustion (blocks SELL).
        /// Now includes momentum protection.
        /// </summary>
        public static bool IsBearishExhaustion(
    MqlRates last,
    MqlRates prev,
    double atrRaw)
        {
            double prevBody = Math.Abs(prev.close - prev.open);
            double lastBody = Math.Abs(last.close - last.open);

            // 1️⃣ Strong bearish impulse before
            bool prevImpulse =
                prevBody >= atrRaw * 0.8 &&
                prev.close < prev.open;

            // 2️⃣ Liquidity sweep below previous low
            bool liquiditySweep = last.low < prev.low;

            // 3️⃣ Failed continuation (did not close below prev low)
            bool failedContinuation = last.close >= prev.low;

            // 4️⃣ Momentum deceleration
            bool deceleration = lastBody < prevBody * 0.5;

            bool condition =
                prevImpulse &&
                liquiditySweep &&
                failedContinuation &&
                deceleration;

            if (condition)
            {
                Console.WriteLine("🔥 Institutional Bearish Exhaustion detected");
                LogFile.Write("🔥 Institutional Bearish Exhaustion detected");
            }

            return condition;
        }

    }


}
