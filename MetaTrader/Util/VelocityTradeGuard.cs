using MetaTrader.Data.Entities;
using MtApi5;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Util
{
    public static class VelocityTradeGuard
    {
        private const int VelocityGraceCandles = 2;

        public static VelocityDecision Evaluate(
            TrackedTrade t,
            bool isBuy,
            double bid,
            double ask,
            double[] emaFast1H,
            double[] emaSlow1H,
            MqlRates[] rates1Hr,
            bool isFirstContinuation,
            bool emaCompressed,
            bool stopAtBreakEven, double atr)
        {
            bool isVelocityTrade = t.Model == EntryModel.Velocity;

            // ---------------------------------------------
            // 📊 Candle tracking
            // ---------------------------------------------
            var currentCandleTime = rates1Hr[^1].time;

            if (currentCandleTime > t.EntryCandleTime)
            {
                t.CandlesSinceEntry++;
                t.EntryCandleTime = currentCandleTime;
            }

            bool inVelocityGrace =
                isVelocityTrade &&
                t.CandlesSinceEntry <= VelocityGraceCandles;

            // ---------------------------------------------
            // 🔄 Structural checks
            // ---------------------------------------------
            bool bearishStructure =
                emaFast1H[^1] < emaSlow1H[^1] &&
                bid < emaFast1H[^1] &&
                (emaFast1H[^1] - emaFast1H[^2]) < 0;

            bool bullishStructure =
                emaFast1H[^1] > emaSlow1H[^1] &&
                ask > emaFast1H[^1] &&
                (emaFast1H[^1] - emaFast1H[^2]) > 0;

            // ---------------------------------------------
            // 🚫 Continuation Failure (disabled during grace)
            // ---------------------------------------------
            if (!inVelocityGrace)
            {
                bool continuationFailed =
                    ContinuationFailureEngine.ShouldExitOnRetrace(
                        rates1Hr,
                        isBuy,
                        bid,
                        ask,
                        0.50,
                        isFirstContinuation,
                        emaCompressed,
                        stopAtBreakEven
                    );

                if (continuationFailed)
                    return VelocityDecision.Exit("ContinuationFailureEngine");
            }

            // ---------------------------------------------
            // 🔥 Fail-Fast (disabled during grace)
            // ---------------------------------------------
            if (!inVelocityGrace)
            {
                if (FailFastExitEngine.ShouldExit(
                        t,
                        ask,
                        bid,
                        emaFast1H,
                        rates1Hr,
                        currentCandleTime,atr))
                {
                    return VelocityDecision.Exit("FailFastExitEngine");
                }
            }

            // ---------------------------------------------
            // 🧱 Structural Kill (post-grace)
            // ---------------------------------------------
            if (isVelocityTrade && !inVelocityGrace)
            {
                if (t.Side == TradeSide.Sell && !bearishStructure)
                    return VelocityDecision.Exit("VelocityStructureBreak");

                if (t.Side == TradeSide.Buy && !bullishStructure)
                    return VelocityDecision.Exit("VelocityStructureBreak");
            }

            return VelocityDecision.Hold(inVelocityGrace);
        }
    }
}
