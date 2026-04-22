using MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Util
{
    public static class ExitDecisionEngine
    {
        public static TradeAction CheckExit(
            bool isBuy,
            MqlRates[] rates1H,
            MqlRates last,
            double atrRaw,
            bool h4Weak,
            bool bullishBreakout,
            bool bearishBreakout
        )
        {
            var prev = rates1H[^2];

            // -----------------------------------------------------------
            // 1️⃣ Dynamic exhaustion candle (applies to both BUY & SELL)
            // -----------------------------------------------------------
            if (DynamicWickRejection.IsBullishExhaustion(last, prev, atrRaw) ||
                DynamicWickRejection.IsBearishExhaustion(last, prev, atrRaw))
            {
                Console.WriteLine("⛔ EXIT: Dynamic exhaustion candle");
                LogFile.Write("⛔ EXIT: Dynamic exhaustion candle");
                return TradeAction.CloseTrade;
            }

            // -----------------------------------------------------------
            // 2️⃣ H4 trend collapse
            // -----------------------------------------------------------
            if (h4Weak)
            {
                Console.WriteLine("⛔ EXIT: H4 trend collapse");
                LogFile.Write("⛔ EXIT: H4 trend collapse");
                return TradeAction.CloseTrade;
            }

            // -----------------------------------------------------------
            // 3️⃣ Opposite breakout (exit immediately)
            // -----------------------------------------------------------
            if (isBuy && bearishBreakout)
            {
                Console.WriteLine("⛔ EXIT: Opposite bearish breakout");
                LogFile.Write("⛔ EXIT: Opposite bearish breakout");
                return TradeAction.CloseTrade;
            }

            if (!isBuy && bullishBreakout)
            {
                Console.WriteLine("⛔ EXIT: Opposite bullish breakout");
                LogFile.Write("⛔ EXIT: Opposite bullish breakout");
                return TradeAction.CloseTrade;
            }

            // No exit signal → Hold
            return TradeAction.Hold;
        }
    }

}


