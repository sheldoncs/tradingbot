using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class ContinuationFailureEngine
    {
        /// <summary>
        /// Determines whether a continuation trade has FAILED
        /// and should be exited early.
        ///
        /// This ONLY applies to the FIRST continuation candle.
        /// Healthy pauses are ignored.
        /// </summary>
        public static bool ShouldExitOnRetrace(
            MqlRates[] rates,
            bool isBuy,
            double bid,
            double ask,
            double retracePercent,
            bool isFirstContinuation,
            bool emaCompressed,
            bool stopAtBreakEven)
        {
            // --------------------------------------------------
            // SAFETY CHECKS
            // --------------------------------------------------
            if (rates == null || rates.Length < 3)
                return false;

            // --------------------------------------------------
            // CONTEXT GATES (CRITICAL)
            // --------------------------------------------------

            // ❌ Do NOT exit if this is not the first continuation candle
            if (!isFirstContinuation)
                return false;

            // ❌ Do NOT exit during compression (pullbacks are normal)
            if (emaCompressed)
                return false;

            // ❌ Do NOT exit if trade is already protected at BE or better
            if (stopAtBreakEven)
                return false;

            // --------------------------------------------------
            // USE LAST CLOSED CANDLE ONLY
            // --------------------------------------------------
            var lastClosed = rates[^1];

            double bodySize = Math.Abs(lastClosed.close - lastClosed.open);

            // Ignore noise candles
            if (bodySize <= double.Epsilon)
                return false;

            // --------------------------------------------------
            // RETRACEMENT FAILURE LOGIC
            // --------------------------------------------------
            if (isBuy)
            {
                // BUY fails if price retraces DOWN into body
                double retraceLevel =
                    lastClosed.close - (bodySize * retracePercent);

                return bid < retraceLevel;
            }
            else
            {
                // SELL fails if price retraces UP into body
                double retraceLevel =
                    lastClosed.close + (bodySize * retracePercent);

                return ask > retraceLevel;
            }
        }
    }


}
