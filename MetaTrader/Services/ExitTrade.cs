using MtApi5;

namespace MetaTrader.Services
{
    public class ExitTrade
    {
        public enum TradeType
        {
            Buy,
            Sell
        }

        public class ExitSignal
        {
            public bool ShouldExit { get; set; }
            public string Reason { get; set; }
        }

        public ExitSignal ShouldExitTrade(
            TradeType type,
            MqlRates[] candles,
            double[] macd,
            double[] macdSignal,
            double[] rsi,
            double[] ema9,
            int i)
        {
            // Make sure we have valid index
            if (i < 1 || i >= candles.Length)
                return new ExitSignal { ShouldExit = false, Reason = "Invalid index" };

            var currentClose = candles[i].close;
            var prevRsi = rsi[i - 1];
            var currentRsi = rsi[i];
            var currentMacd = macd[i];
            var currentMacdSignal = macdSignal[i];
            var currentEma9 = ema9[i];

            var result = new ExitSignal { ShouldExit = false };

            switch (type)
            {
                case TradeType.Buy:
                    // Exit when bullish momentum fades
                    if (currentMacd < currentMacdSignal)
                    {
                        result.ShouldExit = true;
                        result.Reason = "MACD crossover down";
                    }
                    else if (currentRsi < prevRsi && currentRsi > 70)
                    {
                        result.ShouldExit = true;
                        result.Reason = "RSI turning down from overbought";
                    }
                    else if (currentClose < currentEma9)
                    {
                        result.ShouldExit = true;
                        result.Reason = "Close below EMA9";
                    }
                    break;

                case TradeType.Sell:
                    // Exit when bearish momentum fades
                    if (currentMacd > currentMacdSignal)
                    {
                        result.ShouldExit = true;
                        result.Reason = "MACD crossover up";
                    }
                    else if (currentRsi > prevRsi && currentRsi < 30)
                    {
                        result.ShouldExit = true;
                        result.Reason = "RSI rising from oversold";
                    }
                    else if (currentClose > currentEma9)
                    {
                        result.ShouldExit = true;
                        result.Reason = "Close above EMA9";
                    }
                    break;
            }

            return result;
        }

    }
}
