using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using static MetaTrader.Services.ExitTrade;

namespace MetaTrader.Services
{
    public static class BreakEvenAndTightenEngine
    {
        private class Profile
        {
            public double BreakEvenAtProfit { get; set; }
            public double BreakEvenBufferPrice { get; set; }

            public double LockAtProfit { get; set; }
            public double LockProfitAmount { get; set; }

            public double TrailAtProfit { get; set; }
            public double TrailGiveback { get; set; }
        }

        public static void Manage(
            string symbol,
            MtApi5Client client,
            ulong ticket,
            double entryPrice,
            double currentPrice,
            double stopLoss,
            double currentProfitUsd,
            TradeAction tradeAction)
        {
            try
            {
                var profile = GetProfile(symbol);

                LogFile.Write(
                    $"[{symbol}] BE/Tighten check | profit=${currentProfitUsd:F2} | " +
                    $"BE=${profile.BreakEvenAtProfit:F2} | Lock=${profile.LockAtProfit:F2} | Trail=${profile.TrailAtProfit:F2}"
                );

                // =========================
                // 1️⃣ SOFT BREAK EVEN
                // =========================
                if (currentProfitUsd >= profile.BreakEvenAtProfit)
                {
                    double newSL = tradeAction == TradeAction.EnterBuy
                        ? entryPrice - profile.BreakEvenBufferPrice
                        : entryPrice + profile.BreakEvenBufferPrice;

                    bool shouldMove =
                        (tradeAction == TradeAction.EnterBuy && stopLoss < newSL) ||
                        (tradeAction == TradeAction.EnterSell && stopLoss > newSL);

                    if (shouldMove)
                    {
                        client.PositionModify(ticket, newSL, 0);

                        LogFile.Write(
                            $"[{symbol}] 🔹 SL → Soft Break-Even | profit=${currentProfitUsd:F2} | entry={entryPrice:F5} | newSL={newSL:F5}"
                        );
                    }
                }

                // =========================
                // 2️⃣ PROFIT LOCK
                // =========================
                if (currentProfitUsd >= profile.LockAtProfit)
                {
                    double protectedProfit = profile.LockProfitAmount;

                    double priceDistance = Math.Abs(currentPrice - entryPrice);
                    double dollarsPerPriceUnit =
                        currentProfitUsd / Math.Max(priceDistance, 0.0000001);

                    double protectedPriceDistance =
                        protectedProfit / Math.Max(dollarsPerPriceUnit, 0.0000001);

                    double newSL = tradeAction == TradeAction.EnterBuy
                        ? entryPrice + protectedPriceDistance
                        : entryPrice - protectedPriceDistance;

                    bool shouldLock =
                        (tradeAction == TradeAction.EnterBuy && newSL > stopLoss) ||
                        (tradeAction == TradeAction.EnterSell && newSL < stopLoss);

                    if (shouldLock)
                    {
                        client.PositionModify(ticket, newSL, 0);

                        LogFile.Write(
                            $"[{symbol}] 🟢 Profit locked | profit=${currentProfitUsd:F2} | protected=${protectedProfit:F2} | newSL={newSL:F5}"
                        );
                    }
                }

                // =========================
                // 3️⃣ DOLLAR TRAILING
                // =========================
                if (currentProfitUsd >= profile.TrailAtProfit)
                {
                    double protectedProfit = currentProfitUsd - profile.TrailGiveback;

                    if (protectedProfit <= 0)
                        return;

                    double priceDistance = Math.Abs(currentPrice - entryPrice);
                    double dollarsPerPriceUnit =
                        currentProfitUsd / Math.Max(priceDistance, 0.0000001);

                    double protectedPriceDistance =
                        protectedProfit / Math.Max(dollarsPerPriceUnit, 0.0000001);

                    double newSL = tradeAction == TradeAction.EnterBuy
                        ? entryPrice + protectedPriceDistance
                        : entryPrice - protectedPriceDistance;

                    bool shouldTrail =
                        (tradeAction == TradeAction.EnterBuy && newSL > stopLoss) ||
                        (tradeAction == TradeAction.EnterSell && newSL < stopLoss);

                    if (shouldTrail)
                    {
                        client.PositionModify(ticket, newSL, 0);

                        LogFile.Write(
                            $"[{symbol}] 🚀 Dollar trailing active | profit=${currentProfitUsd:F2} | protected=${protectedProfit:F2} | newSL={newSL:F5}"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                LogFile.Write($"[{symbol}] BreakEvenAndTightenEngine error: {ex.Message}");
            }
        }

        private static Profile GetProfile(string symbol)
        {
            string s = symbol.ToUpperInvariant();

            if (s.Contains("XAU") || s.Contains("XAG"))
            {
                return new Profile
                {
                    BreakEvenAtProfit = 5.00,
                    BreakEvenBufferPrice = 1.50,

                    LockAtProfit = 8.00,
                    LockProfitAmount = 2.00,

                    TrailAtProfit = 12.00,
                    TrailGiveback = 5.00
                };
            }

            if (s.Contains("BNB"))
            {
                return new Profile
                {
                    BreakEvenAtProfit = 5.00,
                    BreakEvenBufferPrice = 0.50,

                    LockAtProfit = 8.00,
                    LockProfitAmount = 2.00,

                    TrailAtProfit = 12.00,
                    TrailGiveback = 4.00
                };
            }

            if (s.Contains("SOL"))
            {
                return new Profile
                {
                    BreakEvenAtProfit = 5.00,
                    BreakEvenBufferPrice = 0.30,

                    LockAtProfit = 8.00,
                    LockProfitAmount = 2.00,

                    TrailAtProfit = 12.00,
                    TrailGiveback = 4.00
                };
            }

            if (s.Contains("ETH") || s.Contains("BTC"))
            {
                return new Profile
                {
                    BreakEvenAtProfit = 6.00,
                    BreakEvenBufferPrice = s.Contains("BTC") ? 20.00 : 2.00,

                    LockAtProfit = 10.00,
                    LockProfitAmount = 3.00,

                    TrailAtProfit = 15.00,
                    TrailGiveback = 5.00
                };
            }

            if (s.Contains("US100") || s.Contains("NAS") || s.Contains("USTEC"))
            {
                return new Profile
                {
                    BreakEvenAtProfit = 6.00,
                    BreakEvenBufferPrice = 3.00,

                    LockAtProfit = 10.00,
                    LockProfitAmount = 3.00,

                    TrailAtProfit = 15.00,
                    TrailGiveback = 5.00
                };
            }

            // FX default
            return new Profile
            {
                BreakEvenAtProfit = 3.00,
                BreakEvenBufferPrice = 0.00030,

                LockAtProfit = 5.00,
                LockProfitAmount = 1.50,

                TrailAtProfit = 8.00,
                TrailGiveback = 2.50
            };
        }
    }
}
