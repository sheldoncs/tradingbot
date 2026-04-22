using MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class MasterEngine
    {
        public static TradeAction Process(
            double ask, double bid,
            MqlRates[] rates,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw,
            bool? isBuy,
            string symbol)
        {
            var last = rates[^1];
            var prev = rates[^2];

            double midPrice = (ask + bid) / 2.0;
            double atrPoints = atrRaw * 10.0;

            //---------------------------------------------------------
            // 🔹 1. Update Zone Memory (for future trades)
            //---------------------------------------------------------
            ZoneMemoryEngine.UpdateZones(prev);

            //---------------------------------------------------------
            // 🔹 2. Dynamic Wick Rejection (Exhaustion Filter)
            //---------------------------------------------------------
            if (DynamicWickRejection.IsBullishExhaustion(last, prev, atrRaw))
                return TradeAction.DoNothing;

            if (DynamicWickRejection.IsBearishExhaustion(last, prev, atrRaw))
                return TradeAction.DoNothing;

            //---------------------------------------------------------
            // 🔹 3. Candle Strength Scoring
            //---------------------------------------------------------
            double candleScore = CandleStrengthEngine.ScoreCandle(last, atrRaw);

            Console.WriteLine($"CandleScore = {candleScore:F1}");
            LogFile.Write($"CandleScore = {candleScore:F1}");

            // extremely weak candle? skip
            if (candleScore < -60)
                return TradeAction.DoNothing;

            //---------------------------------------------------------
            // 🔹 4. Dynamic Momentum Calculation
            //---------------------------------------------------------
            double momentumScore = MomentumScoringEngine.ScoreMomentum(
                last, prev, emaFast1H, atrRaw);

            double dynamicThreshold = MomentumScoringEngine.DynamicThreshold(atrRaw);

            Console.WriteLine($"MomentumScore={momentumScore:F2} Thr={dynamicThreshold:F2}");
            LogFile.Write($"MomentumScore={momentumScore:F2} Thr={dynamicThreshold:F2}");

            bool momentumStrong = momentumScore >= dynamicThreshold;

            //---------------------------------------------------------
            // 🔹 5. Breakout & Retest Logic
            //---------------------------------------------------------
            bool retestBuy = BreakoutRetestEngine.IsValidRetestBuy(last, prev);
            bool retestSell = BreakoutRetestEngine.IsValidRetestSell(last, prev);

            //---------------------------------------------------------
            // 🔹 6. Zone Awareness (Avoid Trading Into Support/Resistance)
            //---------------------------------------------------------
            bool nearSupport = ZoneMemoryEngine.IsNearSupport(midPrice, atrRaw);
            bool nearResistance = ZoneMemoryEngine.IsNearResistance(midPrice, atrRaw);

            if (nearSupport)
            {
                Console.WriteLine("⚠ Price near SUPPORT — Buy only, avoid sells");
                LogFile.Write("⚠ Price near SUPPORT — Buy only, avoid sells");

                if (isBuy == false)  // actively in SELL
                    return TradeAction.CloseTrade;

                if (retestBuy && momentumStrong)
                    return TradeAction.EnterBuy;

                return TradeAction.Hold;
            }

            if (nearResistance)
            {
                Console.WriteLine("⚠ Price near RESISTANCE — Sell only, avoid buys");
                LogFile.Write("⚠ Price near RESISTANCE — Sell only, avoid buys");

                if (isBuy == true)
                    return TradeAction.CloseTrade;

                if (retestSell && momentumStrong)
                    return TradeAction.EnterSell;

                return TradeAction.Hold;
            }

            //---------------------------------------------------------
            // 🔹 7. Exit Logic (when in a trade)
            //---------------------------------------------------------
            if (isBuy != null)
            {
                bool exit = MomentumExitEngine.ShouldExit(
                    momentumScore,
                    MomentumScoringEngine.LastMomentumScore);

                if (exit)
                {
                    Console.WriteLine("❌ Momentum collapse detected — closing trade");
                    LogFile.Write("❌ Momentum collapse detected — closing trade");
                    return TradeAction.CloseTrade;
                }
            }

            //---------------------------------------------------------
            // 🔹 8. Entry Logic (when no trade open)
            //---------------------------------------------------------
            if (isBuy == null)
            {
                // BUY SETUPS
                if (momentumStrong || retestBuy)
                {
                    if (!nearResistance)
                        return TradeAction.EnterBuy;
                }

                // SELL SETUPS
                if (momentumStrong || retestSell)
                {
                    if (!nearSupport)
                        return TradeAction.EnterSell;
                }
            }

            //---------------------------------------------------------
            // No action → stay in position or do nothing
            //---------------------------------------------------------
            return TradeAction.Hold;
        }
    }

}
