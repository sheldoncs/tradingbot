using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services.Exits
{
    public static class MomentumFailureExit
    {
        private class Profile
        {
            public double WeakProgressRiskMultiple;
            public int OppositeCandlesRequired;
            public double MinBodyRatio;
            public double EmaLossAtr;
        }

        public static bool ShouldForceExit(
            TrackedTrade trade,
            string symbol,
            MqlRates[] rates,
            double emaFast,
            double atr)
        {
            if (trade == null ||
                rates == null ||
                rates.Length < 3 ||
                atr <= 0)
                return false;

            // IMPORTANT:
            // rates[^1] = latest CLOSED candle
            var c0 = rates[^1];
            var c1 = rates[^2];

            bool isBuy = trade.Side == TradeSide.Buy;
            bool isSell = trade.Side == TradeSide.Sell;

            if (!isBuy && !isSell)
                return false;

            var p = GetProfile(symbol);

            // -----------------------------------------
            // CONDITION 1: Weak progress
            // -----------------------------------------

            double risk = trade.InitialRiskUsd;

            if (risk <= 0)
                return false;

            bool weakProgress =
                trade.MaxProfitUsd <
                risk * p.WeakProgressRiskMultiple;

            if (!weakProgress)
                return false;

            // -----------------------------------------
            // Candle quality
            // -----------------------------------------

            double range0 = Math.Max(c0.high - c0.low, 0.0000001);
            double range1 = Math.Max(c1.high - c1.low, 0.0000001);

            double body0 = Math.Abs(c0.close - c0.open);
            double body1 = Math.Abs(c1.close - c1.open);

            double ratio0 = body0 / range0;
            double ratio1 = body1 / range1;

            bool c0Bearish = c0.close < c0.open;
            bool c1Bearish = c1.close < c1.open;

            bool c0Bullish = c0.close > c0.open;
            bool c1Bullish = c1.close > c1.open;

            // -----------------------------------------
            // Momentum shift
            // -----------------------------------------

            int oppositeCandles = 0;

            if (isBuy)
            {
                if (c0Bearish && ratio0 >= p.MinBodyRatio)
                    oppositeCandles++;

                if (c1Bearish && ratio1 >= p.MinBodyRatio)
                    oppositeCandles++;
            }

            if (isSell)
            {
                if (c0Bullish && ratio0 >= p.MinBodyRatio)
                    oppositeCandles++;

                if (c1Bullish && ratio1 >= p.MinBodyRatio)
                    oppositeCandles++;
            }

            bool momentumFailure =
                oppositeCandles >= p.OppositeCandlesRequired;

            if (!momentumFailure)
                return false;

            // -----------------------------------------
            // EMA9 failure
            // -----------------------------------------

            double emaDistance =
                isBuy
                    ? emaFast - c0.close
                    : c0.close - emaFast;

            bool lostEma =
                emaDistance >= atr * p.EmaLossAtr;

            if (!lostEma)
                return false;

            // -----------------------------------------
            // EXIT
            // -----------------------------------------

            LogFile.Write(
                $"[{symbol}] Early exit: momentum failure | " +
                $"oppCandles={oppositeCandles}, " +
                $"emaDist={emaDistance:F5}, " +
                $"body0={ratio0:F2}, body1={ratio1:F2}, " +
                $"maxProfit={trade.MaxProfitUsd:F2}"
            );

            return true;
        }

        private static Profile GetProfile(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            // GOLD
            if (s.Contains("XAU") || s.Contains("XAG"))
            {
                return new Profile
                {
                    WeakProgressRiskMultiple = 0.35,
                    OppositeCandlesRequired = 2,
                    MinBodyRatio = 0.55,
                    EmaLossAtr = 0.18
                };
            }

            // BTC
            if (s.Contains("BTC"))
            {
                return new Profile
                {
                    WeakProgressRiskMultiple = 0.45,
                    OppositeCandlesRequired = 2,
                    MinBodyRatio = 0.60,
                    EmaLossAtr = 0.30
                };
            }

            // ETH / SOL / BNB
            if (s.Contains("ETH") ||
                s.Contains("SOL") ||
                s.Contains("BNB"))
            {
                return new Profile
                {
                    WeakProgressRiskMultiple = 0.40,
                    OppositeCandlesRequired = 2,
                    MinBodyRatio = 0.58,
                    EmaLossAtr = 0.25
                };
            }

            // FX
            return new Profile
            {
                WeakProgressRiskMultiple = 0.30,
                OppositeCandlesRequired = 2,
                MinBodyRatio = 0.50,
                EmaLossAtr = 0.15
            };
        }
    }
}
