using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using static MetaTrader.Services.ExitTrade;

namespace MetaTrader.Services
{
    public static class BreakEvenAndTightenEngine
    {
        public static void Manage(
            string symbol,
            MtApi5Client client,
            ulong ticket,
            double entryPrice,
            double currentPrice,
            double stopLoss,
            double atr,
            TradeAction tradeAction)
        {
            try
            {
                if (atr <= 0) return;

                // =========================
                // DETECT ASSET TYPE
                // =========================
                bool isGold = symbol.Contains("XAU");
                bool isCrypto = symbol.Contains("USD") && !symbol.Contains("XAU") && !symbol.Contains("US100");
                bool isIndex = symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("USTEC");
                bool isFx = symbol.Contains("USD") && !isCrypto && !isGold;

                // =========================
                // SETTINGS PER ASSET
                // =========================
                double beATR = 1.0;
                double lockATR = 1.8;
                double trailATR = 2.5;

                double bufferATR = 0.4;
                double lockBufferATR = 0.6;

                if (isGold)
                {
                    beATR = 0.8;
                    lockATR = 1.5;
                    trailATR = 2.2;
                    bufferATR = 0.3;
                    lockBufferATR = 0.5;
                }
                else if (isIndex)
                {
                    beATR = 1.2;
                    lockATR = 2.0;
                    trailATR = 3.0;
                    bufferATR = 0.5;
                    lockBufferATR = 0.7;
                }
                else if (isFx)
                {
                    beATR = 1.0;
                    lockATR = 1.6;
                    trailATR = 2.3;
                    bufferATR = 0.35;
                    lockBufferATR = 0.55;
                }
                else if (isCrypto)
                {
                    beATR = 1.0;
                    lockATR = 1.8;
                    trailATR = 2.6;
                    bufferATR = 0.4;
                    lockBufferATR = 0.6;
                }

                // =========================
                // PRICE MOVE
                // =========================
                double move = tradeAction == TradeAction.EnterBuy
                    ? currentPrice - entryPrice
                    : entryPrice - currentPrice;

                // =========================
                // 1️⃣ BREAK EVEN
                // =========================
                if (move >= atr * beATR)
                {
                    double newSL = entryPrice;

                    bool shouldMove =
                        (tradeAction == TradeAction.EnterBuy && stopLoss < newSL) ||
                        (tradeAction == TradeAction.EnterSell && stopLoss > newSL);

                    if (shouldMove)
                    {
                        client.PositionModify(ticket, newSL, 0);
                        LogFile.Write($"[{symbol}] 🔹 SL → Break-Even");
                    }
                }

                // =========================
                // 2️⃣ PROFIT LOCK
                // =========================
                if (move >= atr * lockATR)
                {
                    double newSL = tradeAction == TradeAction.EnterBuy
                        ? entryPrice + atr * lockBufferATR
                        : entryPrice - atr * lockBufferATR;

                    bool shouldLock =
                        (tradeAction == TradeAction.EnterBuy && newSL > stopLoss) ||
                        (tradeAction == TradeAction.EnterSell && newSL < stopLoss);

                    if (shouldLock)
                    {
                        client.PositionModify(ticket, newSL, 0);
                        LogFile.Write($"[{symbol}] 🟢 Profit locked");
                    }
                }

                // =========================
                // 3️⃣ TRAILING (RUNNER MODE)
                // =========================
                if (move >= atr * trailATR)
                {
                    double newSL = tradeAction == TradeAction.EnterBuy
                        ? currentPrice - atr * bufferATR
                        : currentPrice + atr * bufferATR;

                    bool shouldTrail =
                        (tradeAction == TradeAction.EnterBuy && newSL > stopLoss) ||
                        (tradeAction == TradeAction.EnterSell && newSL < stopLoss);

                    if (shouldTrail)
                    {
                        client.PositionModify(ticket, newSL, 0);
                        LogFile.Write($"[{symbol}] 🚀 Trailing stop active");
                    }
                }
            }
            catch (Exception ex)
            {
                LogFile.Write($"[{symbol}] ProfitEngine error: {ex.Message}");
            }
        }
    }
}
