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
            double bid,
            double ask,
            double atr,
            Action<TrackedTrade> ModifyTrade)
        {
            if (t == null)
                return;

            if (atr <= 0)
                return;

            if (ModifyTrade == null)
                return;

            string symbol =
                t.Symbol?.ToUpperInvariant() ?? "";

            if (!symbol.Contains("XAU") &&
                !symbol.Contains("GOLD"))
            {
                return;
            }


            // ==================================================
            // 1. ATR SANITY CHECK
            //
            // Gold H1 ATR should not realistically be a few
            // cents. If something is wrong with the ATR feed,
            // do NOT modify the trade.
            // ==================================================

            if (atr < 0.50)
            {
                LogFile.Write(
                    $"[{t.Symbol}] ⚠️ GOLD BE LOCK skipped: suspicious ATR | " +
                    $"ATR={atr:F5} | ticket={t.Ticket}"
                );

                return;
            }


            // ==================================================
            // 2. CURRENT EXECUTABLE PRICE
            //
            // BUY closes at Bid.
            // SELL closes at Ask.
            // ==================================================

            double currentPrice =
                t.Side == TradeSide.Buy
                    ? bid
                    : ask;


            // ==================================================
            // 3. FAVORABLE MOVE FROM ENTRY
            // ==================================================

            double favorableMove =
                t.Side == TradeSide.Sell
                    ? t.EntryPrice - currentPrice
                    : currentPrice - t.EntryPrice;


            // Trade is not yet in profit.
            if (favorableMove <= 0)
                return;


            double moveAtr =
                favorableMove / atr;


            // ==================================================
            // 4. WAIT FOR MEANINGFUL PROFIT
            //
            // Do not tighten Gold too early.
            // ==================================================

            const double MIN_MOVE_ATR = 0.60;

            if (moveAtr < MIN_MOVE_ATR)
                return;


            // ==================================================
            // 5. PROPOSE A SMALL PROFIT LOCK
            //
            // Lock only a small amount beyond entry so the
            // trade still has breathing room.
            // ==================================================

            double lockDistance =
                atr * 0.08;


            double proposedSL =
                t.Side == TradeSide.Buy
                    ? t.EntryPrice + lockDistance
                    : t.EntryPrice - lockDistance;


            // ==================================================
            // 6. SAFETY DISTANCE FROM CURRENT PRICE
            //
            // BUY:
            // SL must be BELOW Bid.
            //
            // SELL:
            // SL must be ABOVE Ask.
            //
            // We keep an ATR-based safety buffer so MT5 does
            // not reject the stop as invalid / too close.
            // ==================================================

            double minimumStopDistance =
                atr * 0.10;


            double validSL;


            if (t.Side == TradeSide.Buy)
            {
                double highestValidSL =
                    bid - minimumStopDistance;


                validSL =
                    Math.Min(
                        proposedSL,
                        highestValidSL
                    );


                // Still not a valid profitable lock.
                if (validSL <= t.EntryPrice)
                {
                    LogFile.Write(
                        $"[{t.Symbol}] GOLD BE LOCK BUY hold: " +
                        $"not enough room to place profitable SL | " +
                        $"entry={t.EntryPrice:F5} | " +
                        $"bid={bid:F5} | " +
                        $"proposedSL={proposedSL:F5} | " +
                        $"validSL={validSL:F5}"
                    );

                    return;
                }


                // Never move a BUY stop backwards.
                if (t.StopLoss != 0 &&
                    validSL <= t.StopLoss)
                {
                    return;
                }
            }
            else
            {
                double lowestValidSL =
                    ask + minimumStopDistance;


                validSL =
                    Math.Max(
                        proposedSL,
                        lowestValidSL
                    );


                // Still not a valid profitable lock.
                if (validSL >= t.EntryPrice)
                {
                    LogFile.Write(
                        $"[{t.Symbol}] GOLD BE LOCK SELL hold: " +
                        $"not enough room to place profitable SL | " +
                        $"entry={t.EntryPrice:F5} | " +
                        $"ask={ask:F5} | " +
                        $"proposedSL={proposedSL:F5} | " +
                        $"validSL={validSL:F5}"
                    );

                    return;
                }


                // Never move a SELL stop backwards.
                if (t.StopLoss != 0 &&
                    validSL >= t.StopLoss)
                {
                    return;
                }
            }


            // ==================================================
            // 7. APPLY MODIFICATION
            // ==================================================

            double oldSL =
                t.StopLoss;


            t.StopLoss =
                validSL;


            LogFile.Write(
                $"[{t.Symbol}] 🔒 GOLD BE LOCK | " +
                $"ticket={t.Ticket} | " +
                $"side={t.Side} | " +
                $"oldSL={oldSL:F5} → newSL={validSL:F5} | " +
                $"entry={t.EntryPrice:F5} | " +
                $"bid={bid:F5} | " +
                $"ask={ask:F5} | " +
                $"ATR={atr:F5} | " +
                $"favorableMove={favorableMove:F2} | " +
                $"moveATR={moveAtr:F2}"
            );


            try
            {
                ModifyTrade(t);

                LockedTickets.Add(
                    t.Ticket
                );
            }
            catch (Exception ex)
            {
                // Restore the old SL in memory if broker update fails.
                t.StopLoss =
                    oldSL;

                LogFile.Write(
                    $"[{t.Symbol}] ❌ GOLD BE LOCK modification failed | " +
                    $"ticket={t.Ticket} | " +
                    $"attemptedSL={validSL:F5} | " +
                    $"error={ex.Message}"
                );
            }
        }
    }
}
