using MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class ContinuationValidator
    {
        private class Profile
        {
            public double PullbackAtr;
            public double OppositeCandleAtr;
        }

        private static Profile GetProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
            {
                return new Profile
                {
                    PullbackAtr = 0.7,
                    OppositeCandleAtr = 0.6
                };
            }

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
            {
                return new Profile
                {
                    PullbackAtr = 1.0,
                    OppositeCandleAtr = 0.8
                };
            }

            if (symbol.Contains("USD") &&
                (symbol.Contains("ETH") || symbol.Contains("BTC") ||
                 symbol.Contains("SOL") || symbol.Contains("AVAX") ||
                 symbol.Contains("BNB") || symbol.Contains("XRP")))
            {
                return new Profile
                {
                    PullbackAtr = 1.2,   // crypto needs more room
                    OppositeCandleAtr = 1.0
                };
            }

            // FX default
            return new Profile
            {
                PullbackAtr = 0.5,
                OppositeCandleAtr = 0.4
            };
        }

        public static bool IsHealthyContinuation(
            string symbol,
            TradeAction direction,
            MqlRates[] rates,
            double[] emaFast,
            double atr)
        {
            if (rates == null || rates.Length < 3)
                return false;

            var profile = GetProfile(symbol);

            var c0 = rates[^1];
            var c1 = rates[^2];

            double ema9 = emaFast[^1];
            double pullbackDepth = Math.Abs(c0.close - c1.close);

            // --------------------------------
            // BUY
            // --------------------------------
            if (direction == TradeAction.EnterBuy)
            {
                bool aboveEma = c0.close > ema9;
                bool shallowPullback = pullbackDepth < atr * profile.PullbackAtr;
                bool noStrongBear = (c0.open - c0.close) < atr * profile.OppositeCandleAtr;

                return aboveEma && shallowPullback && noStrongBear;
            }

            // --------------------------------
            // SELL
            // --------------------------------
            if (direction == TradeAction.EnterSell)
            {
                bool belowEma = c0.close < ema9;
                bool shallowPullback = pullbackDepth < atr * profile.PullbackAtr;
                bool noStrongBull = (c0.close - c0.open) < atr * profile.OppositeCandleAtr;

                return belowEma && shallowPullback && noStrongBull;
            }

            return false;
        }
    }
}
