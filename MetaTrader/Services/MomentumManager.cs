using MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Services
{
   public class MomentumManager
   {
        public TradeAction EvaluateMomentum(double[] emaFast, double[] emaSlow, MqlRates[] rates, int index, bool? isBuy = null)
        {
            if (index < 3) return TradeAction.Hold;

            double momentum = emaFast[index] - emaSlow[index];
            double prevMomentum = emaFast[index - 1] - emaSlow[index - 1];
            double momentumChange = momentum - prevMomentum;

            bool lastBull = rates[index].close > rates[index].open;
            bool prevBull = rates[index - 1].close > rates[index - 1].open;
            bool thirdBull = rates[index - 2].close > rates[index - 2].open;

            bool lastBear = rates[index].close < rates[index].open;
            bool prevBear = rates[index - 1].close < rates[index - 1].open;
            bool thirdBear = rates[index - 2].close < rates[index - 2].open;

            bool twoBullishInRow = lastBull && prevBull;
            bool threeBullishInRow = lastBull && prevBull && thirdBull;
            bool twoBearishInRow = lastBear && prevBear;
            bool threeBearishInRow = lastBear && prevBear && thirdBear;

            // Entry logic (no trade active)
            if (isBuy == null)
            {
                if (prevMomentum <= 0 && momentum > 0 && twoBullishInRow)
                    return TradeAction.EnterBuy;

                if (prevMomentum >= 0 && momentum < 0 && twoBearishInRow)
                    return TradeAction.EnterSell;

                return TradeAction.Hold;
            }

            // Active trade logic
            bool weakeningBullish = isBuy == true && momentum > 0 && momentumChange < 0;
            bool weakeningBearish = isBuy == false && momentum < 0 && momentumChange > 0;

            double safePrev = Math.Abs(prevMomentum) < 0.0001 ? 0.0001 : Math.Abs(prevMomentum);
            double decayRatio = Math.Abs(momentumChange) / safePrev;
            decayRatio = Math.Min(decayRatio, 3.0); // cap to prevent overreaction
            double adaptiveThreshold = 0.8 + (0.5 * (1 - Math.Min(1, Math.Abs(momentum))));

            if (isBuy == false)
            {
                if (twoBullishInRow || threeBullishInRow)
                    return TradeAction.CloseTrade;

                if (Math.Abs(momentum) > 0.3 && (weakeningBearish || decayRatio > adaptiveThreshold))
                    return TradeAction.TightenStop;
            }

            if (isBuy == true)
            {
                if (twoBearishInRow || threeBearishInRow)
                    return TradeAction.CloseTrade;

                if (Math.Abs(momentum) > 0.3 && (weakeningBullish || decayRatio > adaptiveThreshold))
                    return TradeAction.TightenStop;
            }

            return TradeAction.Hold;
        }

        

    }

}
