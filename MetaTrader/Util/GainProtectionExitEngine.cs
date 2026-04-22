using MetaTrader.Data.Entities;
using System;

namespace MetaTrader.Util
{
    public static class GainProtectionExitEngine
    {
        /// <summary>
        /// Prevents a new trade from wiping out a previous realized gain.
        /// </summary>
        public static bool ShouldExit(
            TrackedTrade currentTrade,
            double currentProfitUsd,
            double lastRealizedProfitUsd,
            TradeSide? lastRealizedSide,
            DateTime lastRealizedTime,
            DateTime currentCandleTime,
            double bufferUsd = 0.25 // small cushion
        )
        {
            // Only apply if:
            // 1. Last trade was profitable
            // 2. Same direction
            // 3. Same session / impulse (same candle or next candle)
            if (lastRealizedProfitUsd <= 0)
                return false;

            if (lastRealizedSide != currentTrade.Side)
                return false;

            if (lastRealizedTime < currentCandleTime.AddHours(-1))
                return false;

            // Net protection: do not allow give-back
            if (currentProfitUsd <= -(lastRealizedProfitUsd - bufferUsd))
                return true;

            return false;
        }
    }

}
