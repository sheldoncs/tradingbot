using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class ImpulseEntryEngine
    {
        public static bool TryEnter(
            string symbol,
            MqlRates c0,
            double atr,
            double ema9,
            double ema21,
            double recentHigh,
            double recentLow,
            double bid,
            double ask,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (atr <= 0)
                return false;

            symbol = symbol.ToUpperInvariant();

            double closedBody = Math.Abs(c0.close - c0.open);
            double bodyAtrClosed = closedBody / atr;

            double livePriceBuy = ask;
            double livePriceSell = bid;

            double liveBodyBuy = Math.Abs(livePriceBuy - c0.open);
            double liveBodySell = Math.Abs(livePriceSell - c0.open);

            double bodyAtrLiveBuy = liveBodyBuy / atr;
            double bodyAtrLiveSell = liveBodySell / atr;

            double bodyAtrBuy = Math.Max(bodyAtrClosed, bodyAtrLiveBuy);
            double bodyAtrSell = Math.Max(bodyAtrClosed, bodyAtrLiveSell);

            double distFromEmaBuy = Math.Abs(livePriceBuy - ema9) / atr;
            double distFromEmaSell = Math.Abs(livePriceSell - ema9) / atr;

            double bodyThreshold = GetBodyThreshold(symbol);
            double maxDistance = GetMaxDistance(symbol);
            double counterTrendBoost = GetCounterTrendBoost(symbol);

            bool overallBullish = ema9 > ema21;
            bool overallBearish = ema9 < ema21;

            double buyRequiredBody =
                overallBullish ? bodyThreshold : bodyThreshold + counterTrendBoost;

            double sellRequiredBody =
                overallBearish ? bodyThreshold : bodyThreshold + counterTrendBoost;

            bool impulseBuy =
                bodyAtrBuy > buyRequiredBody &&
                livePriceBuy > recentHigh &&
                livePriceBuy > ema9 &&
                distFromEmaBuy < maxDistance;

            if (impulseBuy)
            {
                action = TradeAction.EnterBuy;

                LogFile.Write(
                    $"[{symbol}] ⚡ IMPULSE BUY | " +
                    $"bodyAtr={bodyAtrBuy:F2}, required={buyRequiredBody:F2}, " +
                    $"live={bodyAtrLiveBuy:F2}, closed={bodyAtrClosed:F2}, " +
                    $"distEma={distFromEmaBuy:F2}, trendAligned={overallBullish}, " +
                    $"ema9={ema9:F2}, ema21={ema21:F2}"
                );

                return true;
            }

            bool impulseSell =
                bodyAtrSell > sellRequiredBody &&
                livePriceSell < recentLow &&
                livePriceSell < ema9 &&
                distFromEmaSell < maxDistance;

            if (impulseSell)
            {
                action = TradeAction.EnterSell;

                LogFile.Write(
                    $"[{symbol}] ⚡ IMPULSE SELL | " +
                    $"bodyAtr={bodyAtrSell:F2}, required={sellRequiredBody:F2}, " +
                    $"live={bodyAtrLiveSell:F2}, closed={bodyAtrClosed:F2}, " +
                    $"distEma={distFromEmaSell:F2}, trendAligned={overallBearish}, " +
                    $"ema9={ema9:F2}, ema21={ema21:F2}"
                );

                return true;
            }

            return false;
        }

        private static double GetBodyThreshold(string symbol)
        {
            if (IsGold(symbol)) return 1.0;
            if (IsIndex(symbol)) return 0.9;
            if (IsCrypto(symbol)) return 0.85;
            if (IsFx(symbol)) return 0.6;

            return 0.9;
        }

        private static double GetMaxDistance(string symbol)
        {
            if (IsGold(symbol)) return 1.8;
            if (IsIndex(symbol)) return 2.0;
            if (IsCrypto(symbol)) return 2.5;
            if (IsFx(symbol)) return 1.6;

            return 2.0;
        }

        private static double GetCounterTrendBoost(string symbol)
        {
            if (IsGold(symbol)) return 0.35;
            if (IsIndex(symbol)) return 0.30;
            if (IsCrypto(symbol)) return 0.25;
            if (IsFx(symbol)) return 0.20;

            return 0.25;
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") || symbol.Contains("XAG");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("SPX") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("DJI") ||
                   symbol.Contains("GER40") ||
                   symbol.Contains("UK100") ||
                   symbol.Contains("JP225") ||
                   symbol.Contains("INDEX") ||
                   symbol.Contains(".CASH");
        }

        private static bool IsFx(string symbol)
        {
            if (symbol.Length < 6)
                return false;

            string[] ccy = { "USD", "EUR", "GBP", "JPY", "AUD", "NZD", "CAD", "CHF" };

            foreach (var baseCcy in ccy)
            {
                foreach (var quoteCcy in ccy)
                {
                    if (baseCcy == quoteCcy)
                        continue;

                    if (symbol.StartsWith(baseCcy) && symbol.Contains(quoteCcy))
                        return true;
                }
            }

            return false;
        }
    }
}
