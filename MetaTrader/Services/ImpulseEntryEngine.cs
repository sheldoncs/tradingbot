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

            // =============================
            // 🔥 ANTI-CHASE + DYNAMIC LOGIC
            // =============================
            double moveFromHigh = (recentHigh - bid) / atr;
            double moveFromLow = (ask - recentLow) / atr;

            double baseMaxImpulseChase =
                IsGold(symbol) ? 1.5 :
                IsIndex(symbol) ? 1.7 :
                IsFx(symbol) ? 1.2 :
                1.6;

            // 🔥 Strength per side
            double strengthBuy = bodyAtrBuy;
            double strengthSell = bodyAtrSell;

            double maxImpulseChaseBuy = baseMaxImpulseChase +
                (strengthBuy > 1.5 ? 0.4 :
                 strengthBuy > 1.2 ? 0.2 : 0.0);

            double maxImpulseChaseSell = baseMaxImpulseChase +
                (strengthSell > 1.5 ? 0.4 :
                 strengthSell > 1.2 ? 0.2 : 0.0);

            // =============================
            // ⚙️ THRESHOLDS
            // =============================
            double bodyThreshold = GetBodyThreshold(symbol);
            double maxDistance = GetMaxDistance(symbol);
            double counterTrendBoost = GetCounterTrendBoost(symbol);

            bool overallBullish = ema9 > ema21;
            bool overallBearish = ema9 < ema21;

            double buyRequiredBody =
                overallBullish ? bodyThreshold : bodyThreshold + counterTrendBoost;

            double sellRequiredBody =
                overallBearish ? bodyThreshold : bodyThreshold + counterTrendBoost;

            // =============================
            // 🔥 EARLY IMPULSE (NEW)
            // =============================

            double distToBreakout = (recentHigh - livePriceBuy) / atr;
            double distToBreakdown = (livePriceSell - recentLow) / atr;

            double earlyDistLimit =
                IsCrypto(symbol) ? 0.4 :
                IsIndex(symbol) ? 0.6 :
                IsGold(symbol) ? 0.5 :
                0.5;

            double earlyBodyRequirement =
                IsCrypto(symbol) ? 0.7 :
                IsIndex(symbol) ? 0.5 :
                IsGold(symbol) ? 0.6 :
                0.5;

            double minEmaDistance =
                IsGold(symbol) ? 0.20 :
                IsCrypto(symbol) ? 0.18 :
                IsIndex(symbol) ? 0.15 :
                                  0.10;   // FX


            bool earlyImpulseBuy =
                bodyAtrBuy > earlyBodyRequirement &&
                livePriceBuy > ema9 &&
                distFromEmaBuy > minEmaDistance &&          // 🔥 NEW
                distFromEmaBuy < maxDistance &&
                distToBreakout > 0 &&
                distToBreakout < earlyDistLimit;

            if (earlyImpulseBuy)
            {
                action = TradeAction.EnterBuy;

                LogFile.Write(
                    $"[{symbol}] ⚡ EARLY IMPULSE BUY | " +
                    $"distToBreakout={distToBreakout:F2}, bodyAtr={bodyAtrBuy:F2}"
                );

                return true;
            }

            bool earlyImpulseSell =
                bodyAtrSell > earlyBodyRequirement &&
                livePriceSell < ema9 &&
                distFromEmaSell > minEmaDistance &&
                distFromEmaSell < maxDistance &&
                distToBreakdown > 0 &&
                distToBreakdown < earlyDistLimit;

            if (earlyImpulseSell)
            {
                action = TradeAction.EnterSell;

                LogFile.Write(
                    $"[{symbol}] ⚡ EARLY IMPULSE SELL | " +
                    $"distToBreakdown={distToBreakdown:F2}, bodyAtr={bodyAtrSell:F2}"
                );

                return true;
            }

            // =============================
            // ⚡ IMPULSE BUY
            // =============================
            bool impulseBuy =
                bodyAtrBuy > buyRequiredBody &&
                livePriceBuy > recentHigh &&
                livePriceBuy > ema9 &&
                distFromEmaBuy < maxDistance &&
                moveFromLow <= maxImpulseChaseBuy;

            if (impulseBuy)
            {
                action = TradeAction.EnterBuy;

                LogFile.Write(
                    $"[{symbol}] ⚡ IMPULSE BUY | " +
                    $"bodyAtr={bodyAtrBuy:F2}, required={buyRequiredBody:F2}, " +
                    $"maxChase={maxImpulseChaseBuy:F2}, moveATR={moveFromLow:F2}, " +
                    $"distEma={distFromEmaBuy:F2}, trendAligned={overallBullish}"
                );

                return true;
            }

            // =============================
            // 🔻 IMPULSE SELL
            // =============================
            bool impulseSell =
                bodyAtrSell > sellRequiredBody &&
                livePriceSell < recentLow &&
                livePriceSell < ema9 &&
                distFromEmaSell < maxDistance &&
                moveFromHigh <= maxImpulseChaseSell;

            if (impulseSell)
            {
                action = TradeAction.EnterSell;

                LogFile.Write(
                    $"[{symbol}] ⚡ IMPULSE SELL | " +
                    $"bodyAtr={bodyAtrSell:F2}, required={sellRequiredBody:F2}, " +
                    $"maxChase={maxImpulseChaseSell:F2}, moveATR={moveFromHigh:F2}, " +
                    $"distEma={distFromEmaSell:F2}, trendAligned={overallBearish}"
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
