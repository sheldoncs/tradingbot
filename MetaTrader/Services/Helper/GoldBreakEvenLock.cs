using MetaTrader.Data.Entities;
using MetaTrader.Util;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services.Helper
{
    public static class GoldBreakEvenLock
    {
        private static readonly HashSet<ulong> LockedTickets = new();
        public static void Apply(
            TrackedTrade t,
            double mid,
            double atr, Action<TrackedTrade> ModifyTrade)
        {
            if (t == null || atr <= 0)
                return;

            if (!t.Symbol.Contains("XAU") && !t.Symbol.Contains("GOLD"))
                return;

            double favorableMove =
                t.Side == TradeSide.Sell
                    ? t.EntryPrice - mid
                    : mid - t.EntryPrice;

            double moveAtr = favorableMove / atr;

            // Wait until gold has moved at least 0.60 ATR in profit
            if (moveAtr < 0.60)
                return;

            // Lock a tiny profit beyond entry, not too close to current price
            double lockProfit = atr * 0.08;

            double newSL =
                t.Side == TradeSide.Buy
                    ? t.EntryPrice + lockProfit
                    : t.EntryPrice - lockProfit;

            if (t.Side == TradeSide.Buy && newSL > t.StopLoss)
            {
                LogFile.Write(
                    $"🔒 GOLD BE LOCK BUY | ticket={t.Ticket} | oldSL={t.StopLoss:F5} → newSL={newSL:F5} | moveATR={moveAtr:F2}"
                );

                t.StopLoss = newSL;
                ModifyTrade(t);
            }

            if (t.Side == TradeSide.Sell && newSL < t.StopLoss)
            {
                LogFile.Write(
                    $"🔒 GOLD BE LOCK SELL | ticket={t.Ticket} | oldSL={t.StopLoss:F5} → newSL={newSL:F5} | moveATR={moveAtr:F2}"
                );

                t.StopLoss = newSL;
                ModifyTrade(t);
            }
        }
    }
}
