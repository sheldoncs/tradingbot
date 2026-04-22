using MetaTrader.Data.Entities;
using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class FailFastExitEngine
    {
        private const int FAIL_FAST_CANDLES = 3;
        private const double MIN_PROGRESS_ATR = 0.25;   // must move at least 0.25 ATR
        private const int REQUIRED_FAIL_SIGNALS = 3;    // was 2 → now 3

        public static bool ShouldExit(
            TrackedTrade trade,
            double ask,
            double bid,
            double[] emaFast,
            MqlRates[] rates,
            DateTime currentCandleTime,
            double atr)
        {
            int candlesSinceEntry = GetCandlesSinceEntry(
                trade.OpenTime,
                currentCandleTime);

            // ❌ Do not fail-fast after early window
            if (candlesSinceEntry > FAIL_FAST_CANDLES)
                return false;

            double mid = (ask + bid) / 2.0;

            // --------------------------------------------------
            // ✅ PROGRESS CHECK — allow breathing room
            // --------------------------------------------------
            double progress =
                trade.Side == TradeSide.Buy
                    ? mid - trade.EntryPrice
                    : trade.EntryPrice - mid;

            if (progress >= atr * MIN_PROGRESS_ATR)
            {
                // Trade has proven itself → no fail-fast
                return false;
            }

            return MomentumCollapsed(
                trade,
                mid,
                emaFast,
                rates);
        }

        private static bool MomentumCollapsed(
            TrackedTrade trade,
            double mid,
            double[] emaFast,
            MqlRates[] rates)
        {
            var last = rates[^1];

            double ema9 = emaFast[^1];
            double ema9Prev = emaFast[^2];
            double emaSlope = ema9 - ema9Prev;

            int failSignals = 0;

            // 1️⃣ Structure break beyond EMA9
            if (trade.Side == TradeSide.Buy && mid < ema9 && last.close < ema9)
                failSignals++;

            if (trade.Side == TradeSide.Sell && mid > ema9 && last.close > ema9)
                failSignals++;

            // 2️⃣ EMA slope reversed (not flat)
            if (trade.Side == TradeSide.Buy && emaSlope < 0)
                failSignals++;

            if (trade.Side == TradeSide.Sell && emaSlope > 0)
                failSignals++;

            // 3️⃣ Weak candle + overlap
            double body = Math.Abs(last.close - last.open);
            double range = last.high - last.low;

            if (range > 0 && body < range * 0.20)
                failSignals++;

            // 4️⃣ Strong opposing rejection
            double upperWick = last.high - Math.Max(last.close, last.open);
            double lowerWick = Math.Min(last.close, last.open) - last.low;

            if (trade.Side == TradeSide.Buy && upperWick > body * 1.5)
                failSignals++;

            if (trade.Side == TradeSide.Sell && lowerWick > body * 1.5)
                failSignals++;

            return failSignals >= REQUIRED_FAIL_SIGNALS;
        }

        private static int GetCandlesSinceEntry(
            DateTime entryTime,
            DateTime currentCandleTime)
        {
            if (currentCandleTime <= entryTime)
                return 0;

            return (int)Math.Floor(
                (currentCandleTime - entryTime).TotalHours);
        }
    }

}
