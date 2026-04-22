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
        public static int Lookback = 10;

        // --------------------------------
        // SYMBOL TYPE DETECTION
        // --------------------------------
        private static bool IsGold(string symbol)
            => symbol.Contains("XAU");

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
            if (IsGold(symbol)) return 0.6;
            if (IsFx(symbol)) return 0.5;
            if (IsIndex(symbol)) return 0.5;
            if (IsCrypto(symbol)) return 0.4;

            return 0.5;
        }

        private static double GetBreakoutRangeFactor(string symbol)
        {
            if (IsGold(symbol)) return 1.4;
            if (IsFx(symbol)) return 1.3;
            if (IsIndex(symbol)) return 1.2;
            if (IsCrypto(symbol)) return 1.1;

            return 1.2;
        }

        private static double GetBreakoutBodyRatio(string symbol)
        {
            if (IsGold(symbol)) return 0.65;
            if (IsFx(symbol)) return 0.62;
            if (IsIndex(symbol)) return 0.58;
            if (IsCrypto(symbol)) return 0.55;

            return 0.6;
        }

        // --------------------------------
        // LEVEL DETECTION
        // --------------------------------
        private static (double highest, double lowest) GetLevels(MqlRates[] rates)
        {
            double highest = double.MinValue;
            double lowest = double.MaxValue;

            for (int i = 2; i < Lookback + 2; i++)
            {
                if (rates[^i].high > highest)
                    highest = rates[^i].high;

                if (rates[^i].low < lowest)
                    lowest = rates[^i].low;
            }

            return (highest, lowest);
        }

        public static bool IsNearResistance(
            string symbol,
            MqlRates[] rates,
            double atr,
            double currentPrice)
        {
            var (highest, _) = GetLevels(rates);
            double buffer = atr * GetBufferFactor(symbol);

            return currentPrice >= (highest - buffer);
        }

        public static bool IsNearSupport(
            string symbol,
            MqlRates[] rates,
            double atr,
            double currentPrice)
        {
            var (_, lowest) = GetLevels(rates);
            double buffer = atr * GetBufferFactor(symbol);

            return currentPrice <= (lowest + buffer);
        }

        // --------------------------------
        // BREAKOUT LOGIC
        // --------------------------------
        private static bool IsStrongBreakout(
            string symbol,
            MqlRates[] rates,
            double atr)
        {
            var c0 = rates[^1];

            double range = c0.high - c0.low;
            if (range <= 0) return false;

            double body = Math.Abs(c0.close - c0.open);
            double ratio = body / range;

            return range > atr * GetBreakoutRangeFactor(symbol)
                && ratio > GetBreakoutBodyRatio(symbol);
        }

        private static bool IsBreakingSupport(
            MqlRates[] rates,
            double lowest)
        {
            var c0 = rates[^1];
            return c0.close < lowest; // clean break
        }

        private static bool IsBreakingResistance(
            MqlRates[] rates,
            double highest)
        {
            var c0 = rates[^1];
            return c0.close > highest;
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

            var (highest, lowest) = GetLevels(rates);

            bool nearResistance = currentPrice >= (highest - atr * GetBufferFactor(symbol));
            bool nearSupport = currentPrice <= (lowest + atr * GetBufferFactor(symbol));

            bool strongBreakout = IsStrongBreakout(symbol, rates, atr);

            bool breakingResistance = IsBreakingResistance(rates, highest);
            bool breakingSupport = IsBreakingSupport(rates, lowest);

            // --------------------------------
            // BUY LOGIC
            // --------------------------------
            if (action == TradeAction.EnterBuy)
            {
                // Block only if resistance is HOLDING
                if (nearResistance && !breakingResistance && !strongBreakout)
                {
                    LogFile.Write($"[{symbol}] ❌ Buy blocked: holding resistance");
                    return true;
                }
            }

            // --------------------------------
            // SELL LOGIC
            // --------------------------------
            if (action == TradeAction.EnterSell)
            {
                // Block only if support is HOLDING
                if (nearSupport && !breakingSupport && !strongBreakout)
                {
                    LogFile.Write($"[{symbol}] ❌ Sell blocked: holding support");
                    return true;
                }
            }

            return false;
        }
    }
}
