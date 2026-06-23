using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.helper;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class SupportResistanceEngine
    {
        public static int Lookback = 12;

        private class SrLevel
        {
            public double Price;
            public int Touches;
            public bool Valid;
        }

        // --------------------------------
        // SYMBOL TYPE DETECTION
        // --------------------------------
        private static bool IsGold(string symbol)
            => symbol.Contains("XAU") || symbol.Contains("XAG");

        private static bool IsCrypto(string symbol)
            => symbol.Contains("BTC") || symbol.Contains("ETH") ||
               symbol.Contains("SOL") || symbol.Contains("BNB") ||
               symbol.Contains("XRP") || symbol.Contains("ADA") ||
               symbol.Contains("AVAX") || symbol.Contains("LINK");

        private static bool IsIndex(string symbol)
            => symbol.Contains("US100") || symbol.Contains("NAS") ||
               symbol.Contains("USTEC") || symbol.Contains("US30");

        private static bool IsFx(string symbol)
            => !IsGold(symbol) && !IsCrypto(symbol) && !IsIndex(symbol);

        // --------------------------------
        // ADAPTIVE PARAMETERS
        // --------------------------------
        private static double GetBufferFactor(string symbol)
        {
            if (IsGold(symbol)) return 0.65;
            if (IsFx(symbol)) return 0.50;
            if (IsIndex(symbol)) return 0.55;
            if (IsCrypto(symbol)) return 0.45;
            return 0.50;
        }

        private static double GetLevelClusterAtr(string symbol)
        {
            if (IsGold(symbol)) return 0.45;
            if (IsFx(symbol)) return 0.35;
            if (IsIndex(symbol)) return 0.40;
            if (IsCrypto(symbol)) return 0.40;
            return 0.40;
        }

        private static double GetMomentumThreshold(string symbol)
        {
            if (IsGold(symbol)) return 1.35;
            if (IsFx(symbol)) return 1.10;
            if (IsIndex(symbol)) return 1.15;
            if (IsCrypto(symbol)) return 1.20;
            return 1.20;
        }

        // --------------------------------
        // BASIC LEVEL DETECTION
        // --------------------------------
        private static (double highest, double lowest) GetLevels(MqlRates[] rates)
        {
            double highest = double.MinValue;
            double lowest = double.MaxValue;

            for (int i = 2; i < Lookback + 2 && i < rates.Length; i++)
            {
                highest = Math.Max(highest, rates[^i].high);
                lowest = Math.Min(lowest, rates[^i].low);
            }

            return (highest, lowest);
        }

        private static SrLevel GetSupportShelf(string symbol, MqlRates[] rates, double atr)
        {
            double cluster = atr * GetLevelClusterAtr(symbol);

            double bestLevel = 0;
            int bestTouches = 0;

            for (int i = 2; i < Lookback + 2 && i < rates.Length; i++)
            {
                double candidate = rates[^i].low;
                int touches = 0;

                for (int j = 2; j < Lookback + 2 && j < rates.Length; j++)
                {
                    if (Math.Abs(rates[^j].low - candidate) <= cluster ||
                        Math.Abs(Math.Min(rates[^j].open, rates[^j].close) - candidate) <= cluster)
                        touches++;
                }

                if (touches > bestTouches)
                {
                    bestTouches = touches;
                    bestLevel = candidate;
                }
            }

            return new SrLevel
            {
                Price = bestLevel,
                Touches = bestTouches,
                Valid = bestTouches >= 2
            };
        }

        private static SrLevel GetResistanceShelf(string symbol, MqlRates[] rates, double atr)
        {
            double cluster = atr * GetLevelClusterAtr(symbol);

            double bestLevel = 0;
            int bestTouches = 0;

            for (int i = 2; i < Lookback + 2 && i < rates.Length; i++)
            {
                double candidate = rates[^i].high;
                int touches = 0;

                for (int j = 2; j < Lookback + 2 && j < rates.Length; j++)
                {
                    if (Math.Abs(rates[^j].high - candidate) <= cluster ||
                        Math.Abs(Math.Max(rates[^j].open, rates[^j].close) - candidate) <= cluster)
                        touches++;
                }

                if (touches > bestTouches)
                {
                    bestTouches = touches;
                    bestLevel = candidate;
                }
            }

            return new SrLevel
            {
                Price = bestLevel,
                Touches = bestTouches,
                Valid = bestTouches >= 2
            };
        }

        // --------------------------------
        // MAIN BLOCK LOGIC (UPDATED)
        // --------------------------------
        public static bool ShouldBlockTrade(
            string symbol,
            TradeAction action,
            MqlRates[] rates,
            double atr,
            double momentum,
            double currentPrice)
        {
            if (rates == null || rates.Length < Lookback + 2 || atr <= 0)
                return false;

            symbol = symbol.ToUpperInvariant();

            var (highest, lowest) = GetLevels(rates);
            var supportShelf = GetSupportShelf(symbol, rates, atr);
            var resistanceShelf = GetResistanceShelf(symbol, rates, atr);

            double activeSupport = supportShelf.Valid ? supportShelf.Price : lowest;
            double activeResistance = resistanceShelf.Valid ? resistanceShelf.Price : highest;

            // --------------------------------
            // 🔥 SPACE CALCULATION
            // --------------------------------
            double distToResistance = Math.Abs(activeResistance - currentPrice) / atr;
            double distToSupport = Math.Abs(currentPrice - activeSupport) / atr;

            double minSpace =
                IsCrypto(symbol) ? 0.6 :
                IsGold(symbol) ? 0.5 :
                IsIndex(symbol) ? 0.5 :
                0.4;

            // --------------------------------
            // 🔥 EMA PROXIMITY (NO CHASING)
            // --------------------------------
            double emaApprox = rates[^1].close; // replace with EMA9 if available
            double distFromEma = Math.Abs(currentPrice - emaApprox) / atr;

            double maxEmaDistance =
                IsCrypto(symbol) ? 1.2 :
                IsGold(symbol) ? 1.0 :
                IsIndex(symbol) ? 1.0 :
                0.8;

            // --------------------------------
            // 🔥 STRONG BREAKOUT CONFIRMATION
            // --------------------------------
            bool strongBreakout =
                Math.Abs(momentum) >= GetMomentumThreshold(symbol);

            bool confirmedBreakout =
                strongBreakout &&
                (action == TradeAction.EnterBuy
                    ? rates[^1].close > activeResistance + atr * 0.2
                    : rates[^1].close < activeSupport - atr * 0.2);

            // --------------------------------
            // BUY LOGIC
            // --------------------------------
            if (action == TradeAction.EnterBuy)
            {
                if (distToResistance < minSpace && !confirmedBreakout)
                {
                    LogFile.Write($"[{symbol}] ❌ Buy blocked: no space above | distATR={distToResistance:F2}");
                    return true;
                }

                if (distFromEma > maxEmaDistance)
                {
                    LogFile.Write($"[{symbol}] ❌ Buy blocked: too far from EMA | distATR={distFromEma:F2}");
                    return true;
                }

                LogFile.Write($"[{symbol}] ✅ Buy allowed | space={distToResistance:F2}, emaDist={distFromEma:F2}");
            }

            // --------------------------------
            // SELL LOGIC
            // --------------------------------
            if (action == TradeAction.EnterSell)
            {
                if (distToSupport < minSpace && !confirmedBreakout)
                {
                    LogFile.Write($"[{symbol}] ❌ Sell blocked: no space below | distATR={distToSupport:F2}");
                    return true;
                }

                if (distFromEma > maxEmaDistance)
                {
                    LogFile.Write($"[{symbol}] ❌ Sell blocked: too far from EMA | distATR={distFromEma:F2}");
                    return true;
                }

                LogFile.Write($"[{symbol}] ✅ Sell allowed | space={distToSupport:F2}, emaDist={distFromEma:F2}");
            }

            return false;
        }
    }
}
