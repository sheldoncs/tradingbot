using MtApi5;
using System;

namespace MetaTrader.Util
{
    public class RejectionFilters
    {
        public static bool IsBearishRejection(MqlRates c, double atr)
        {
            double body = Math.Abs(c.close - c.open);
            double upperWick = c.high - Math.Max(c.close, c.open);
            double lowerWick = Math.Min(c.close, c.open) - c.low;

            // 1️⃣ If candle is a bullish impulse → NOT a rejection
            bool impulseBull =
                body >= atr * 0.40 &&                   // strong body
                (c.high - c.close) <= atr * 0.20;       // close near high

            if (impulseBull)
                return false;

            // 2️⃣ Standard bearish rejection definition
            bool longUpperWick = upperWick >= body * 1.2;     // wick bigger than body
            bool bearishClose = c.close < c.open;             // candle closes bearish

            // 3️⃣ Allow XAUUSD volatility: upper wick can be big but body MUST be weak
            bool weakBody = body <= atr * 0.25;

            // FINAL RULE:
            return longUpperWick && bearishClose && weakBody;
        }

        public static bool IsBullishRejection(MqlRates c, double atr)
        {
            double body = Math.Abs(c.close - c.open);
            double lowerWick = Math.Min(c.close, c.open) - c.low;
            double upperWick = c.high - Math.Max(c.close, c.open);

            // 1️⃣ If candle is a BEARISH impulse → NOT a bullish rejection
            bool impulseBear =
                body >= atr * 0.40 &&                   // strong body
                (c.close - c.low) <= atr * 0.20;        // close near low

            if (impulseBear)
                return false;

            // 2️⃣ Standard bullish rejection: long lower wick + bullish close + weak body
            bool longLowerWick = lowerWick >= body * 1.2;     // wick bigger than body
            bool bullishClose = c.close > c.open;             // candle is green
            bool weakBody = body <= atr * 0.25;               // body must be small

            // FINAL RULE:
            return longLowerWick && bullishClose && weakBody;
        }

        public static bool IsStrongBullishBreakout(MqlRates candle, double atr)
        {
            double body = Math.Abs(candle.close - candle.open);
            double wickUp = candle.high - candle.close;

            // NEW safer & realistic gold rules:
            bool bigBody = body >= atr * 0.40;              // at least 40% ATR
            bool closeNearHigh = (wickUp <= atr * 0.20);    // upper wick <= 20% ATR

            return bigBody || closeNearHigh;   // allow EITHER big body OR strong close
        }

        public static bool IsStrongBearishBreakout(MqlRates c, double atr)
        {
            double body = Math.Abs(c.close - c.open);
            double lowerWick = c.close - c.low;     // wick below close
            double upperWick = c.high - c.close;    // wick above close if bearish candle

            // 1️⃣ Strong bearish body (momentum) 
            bool bigBody = body >= atr * 0.40;      // body at least 40% ATR

            // 2️⃣ Close near the LOW (good bearish pressure)
            bool closeNearLow = lowerWick <= atr * 0.20;

            // 3️⃣ Allow XAU volatility: either big body OR strong close qualifies
            return bigBody || closeNearLow;
        }

    }
}
