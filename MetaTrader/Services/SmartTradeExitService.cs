using MetaTrader.Enums;
using MtApi5;
using System;
using System.Linq;

namespace MetaTrader.Services
{
    public class SmartTradeExitService
    {
       
        public TradeExitAction EvaluateTradeExit(MqlRates[] rates,
            TradeAction currentTrade,
            int fastPeriod = 9,
            int slowPeriod = 21,
            double momentumThreshold = 0.6,
            double slopeThreshold = 0.0003)
        {
            if (rates.Length < slowPeriod + 3)
                return TradeExitAction.Hold;

            int index = rates.Length - 1;

            // --- 1️⃣ Calculate EMAs ---
            double[] fastEmaSeries = CalculateEMAArray(rates, fastPeriod);
            double[] slowEmaSeries = CalculateEMAArray(rates, slowPeriod);

            double fastEma = fastEmaSeries[^1];        // latest value
            double prevFastEma = fastEmaSeries[^2];    // previous value
            double slowEma = slowEmaSeries[^1];
            double prevSlowEma = slowEmaSeries[^2];

            double fastSlope = fastEma - prevFastEma;
            double slowSlope = slowEma - prevSlowEma;


            // --- 3️⃣ Candle Momentum ---
            double body = Math.Abs(rates[index].close - rates[index].open);
            double range = rates[index].high - rates[index].low;
            double momentumStrength = body / (range == 0 ? 1 : range);

            double prevBody = Math.Abs(rates[index - 1].close - rates[index - 1].open);
            bool momentumIncreasing = body > prevBody;

            bool isBullish = rates[index].close > rates[index].open;
            bool isBearish = rates[index].close < rates[index].open;

            // --- 4️⃣ Exit Logic ---
            switch (currentTrade)
            {
                // 🟩 BUY TRADE EXIT RULES
                case TradeAction.EnterBuy:
                    // Strong bearish candle with momentum
                    if (isBearish && momentumStrength > momentumThreshold)
                        return TradeExitAction.CloseAll;

                    // Fast EMA starting to flatten or reverse
                    if (fastSlope < slopeThreshold / 2)
                        return TradeExitAction.CloseHalf;

                    // EMA cross
                    if (fastEma < slowEma)
                        return TradeExitAction.CloseAll;
                    break;

                // 🟥 SELL TRADE EXIT RULES
                case TradeAction.EnterSell:
                    // Strong bullish candle with momentum
                    if (isBullish && momentumStrength > momentumThreshold)
                        return TradeExitAction.CloseAll;

                    // Fast EMA flattening or reversing up
                    if (fastSlope > -slopeThreshold / 2)
                        return TradeExitAction.CloseHalf;

                    // EMA cross
                    if (fastEma > slowEma)
                        return TradeExitAction.CloseAll;
                    break;
            }

            // Default: Hold position
            return TradeExitAction.Hold;
        }

        private static double CalculateEMA(MqlRates[] rates, int period)
        {
            double k = 2.0 / (period + 1);
            double ema = rates[0].close; // starting point

            for (int i = 1; i < rates.Length; i++)
                ema = (rates[i].close - ema) * k + ema;

            return ema;
        }

        public static double[] CalculateEMAArray(MqlRates[] rates, int period)
        {
            double[] closes = rates.Select(r => r.close).ToArray();
            double[] ema = new double[closes.Length];

            double k = 2.0 / (period + 1);
            ema[0] = closes[0]; // seed from first close

            for (int i = 1; i < closes.Length; i++)
                ema[i] = (closes[i] - ema[i - 1]) * k + ema[i - 1];

            return ema;
        }

    }
}
