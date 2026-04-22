using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;

namespace MetaTrader.Services.Exits
{
    public static class MomentumFailureExit
    {
        public static bool ShouldForceExit(
            TrackedTrade trade,
            string symbol,
            MqlRates[] rates,
            double emaFast,   // EMA9
            double atr)
        {
            if (rates == null || rates.Length < 3)
                return false;

            var c0 = rates[^1];
            var c1 = rates[^2];

            bool isBuy = trade.Side == TradeSide.Buy;
            bool isSell = trade.Side == TradeSide.Sell;
            // -----------------------------------------
            // CONDITION 1: Weak progress
            // -----------------------------------------
            double risk = trade.InitialRiskUsd;

            if (risk <= 0)
                return false;

            bool weakProgress = trade.MaxProfitUsd < risk * 0.25;

            if (!weakProgress)
                return false; // let winners breathe

            // -----------------------------------------
            // CONDITION 2: Momentum failure
            // -----------------------------------------
            bool bearishShift =
                isBuy &&
                c0.close < c0.open &&
                c1.close < c1.open;

            bool bullishShift =
                isSell &&
                c0.close > c0.open &&
                c1.close > c1.open;

            // -----------------------------------------
            // CONDITION 3: EMA9 loss
            // -----------------------------------------
            bool lostEma =
                (isBuy && c0.close < emaFast) ||
                (isSell && c0.close > emaFast);

            // -----------------------------------------
            // FINAL DECISION
            // -----------------------------------------
            if ((bearishShift || bullishShift) && lostEma)
            {
                LogFile.Write($"[{symbol}] Early exit: momentum failure");
                return true;
            }

            return false;
        }
    }
}
