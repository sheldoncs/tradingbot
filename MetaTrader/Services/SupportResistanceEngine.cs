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

        private static double GetBreakoutRangeFactor(string symbol)
        {
            if (IsGold(symbol)) return 1.35;
            if (IsFx(symbol)) return 1.25;
            if (IsIndex(symbol)) return 1.20;
            if (IsCrypto(symbol)) return 1.10;

            return 1.20;
        }

        private static double GetBreakoutBodyRatio(string symbol)
        {
            if (IsGold(symbol)) return 0.60;
            if (IsFx(symbol)) return 0.58;
            if (IsIndex(symbol)) return 0.56;
            if (IsCrypto(symbol)) return 0.54;

            return 0.58;
        }

        private static double GetBreakClearanceAtr(string symbol)
        {
            if (IsGold(symbol)) return 0.15;
            if (IsFx(symbol)) return 0.08;
            if (IsIndex(symbol)) return 0.10;
            if (IsCrypto(symbol)) return 0.10;

            return 0.10;
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
                if (rates[^i].high > highest)
                    highest = rates[^i].high;

                if (rates[^i].low < lowest)
                    lowest = rates[^i].low;
            }

            return (highest, lowest);
        }

        // --------------------------------
        // SHELF / CLUSTER DETECTION
        // Finds repeated reactions around support/resistance
        // --------------------------------
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
                    bool lowTouched =
                        Math.Abs(rates[^j].low - candidate) <= cluster;

                    bool bodyTouched =
                        Math.Abs(Math.Min(rates[^j].open, rates[^j].close) - candidate) <= cluster;

                    if (lowTouched || bodyTouched)
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
                    bool highTouched =
                        Math.Abs(rates[^j].high - candidate) <= cluster;

                    bool bodyTouched =
                        Math.Abs(Math.Max(rates[^j].open, rates[^j].close) - candidate) <= cluster;

                    if (highTouched || bodyTouched)
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

        public static bool IsNearResistance(
            string symbol,
            MqlRates[] rates,
            double atr,
            double currentPrice)
        {
            var (highest, _) = GetLevels(rates);
            var shelf = GetResistanceShelf(symbol, rates, atr);

            double buffer = atr * GetBufferFactor(symbol);

            bool nearHighest = currentPrice >= highest - buffer;
            bool nearShelf = shelf.Valid && currentPrice >= shelf.Price - buffer;

            return nearHighest || nearShelf;
        }

        public static bool IsNearSupport(
            string symbol,
            MqlRates[] rates,
            double atr,
            double currentPrice)
        {
            var (_, lowest) = GetLevels(rates);
            var shelf = GetSupportShelf(symbol, rates, atr);

            double buffer = atr * GetBufferFactor(symbol);

            bool nearLowest = currentPrice <= lowest + buffer;
            bool nearShelf = shelf.Valid && currentPrice <= shelf.Price + buffer;

            return nearLowest || nearShelf;
        }

        // --------------------------------
        // BREAKOUT LOGIC
        // --------------------------------
        private static bool IsStrongBreakout(
            string symbol,
            MqlRates[] rates,
            double atr,
            double momentum)
        {
            var c0 = rates[^1];

            double range = c0.high - c0.low;
            if (range <= 0) return false;

            double body = Math.Abs(c0.close - c0.open);
            double ratio = body / range;

            bool strongCandle =
                range > atr * GetBreakoutRangeFactor(symbol) &&
                ratio > GetBreakoutBodyRatio(symbol);

            bool strongMomentum =
                Math.Abs(momentum) >= GetMomentumThreshold(symbol);

            return strongCandle && strongMomentum;
        }

        private static bool IsBreakingSupport(
            string symbol,
            MqlRates[] rates,
            double support,
            double atr,
            double momentum)
        {
            var c0 = rates[^1];

            double clearance = atr * GetBreakClearanceAtr(symbol);

            bool closeBelow =
                c0.close < support - clearance;

            bool bearishCandle =
                c0.close < c0.open;

            bool strongMomentum =
                Math.Abs(momentum) >= GetMomentumThreshold(symbol);

            return closeBelow && bearishCandle && strongMomentum;
        }

        private static bool IsBreakingResistance(
            string symbol,
            MqlRates[] rates,
            double resistance,
            double atr,
            double momentum)
        {
            var c0 = rates[^1];

            double clearance = atr * GetBreakClearanceAtr(symbol);

            bool closeAbove =
                c0.close > resistance + clearance;

            bool bullishCandle =
                c0.close > c0.open;

            bool strongMomentum =
                Math.Abs(momentum) >= GetMomentumThreshold(symbol);

            return closeAbove && bullishCandle && strongMomentum;
        }

        // --------------------------------
        // MAIN BLOCK LOGIC
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

            double activeSupport =
                supportShelf.Valid ? supportShelf.Price : lowest;

            double activeResistance =
                resistanceShelf.Valid ? resistanceShelf.Price : highest;

            double buffer = atr * GetBufferFactor(symbol);

            bool nearResistance =
                currentPrice >= activeResistance - buffer;

            bool nearSupport =
                currentPrice <= activeSupport + buffer;

            bool strongBreakout =
                IsStrongBreakout(symbol, rates, atr, momentum);

            bool breakingResistance =
                IsBreakingResistance(symbol, rates, activeResistance, atr, momentum);

            bool breakingSupport =
                IsBreakingSupport(symbol, rates, activeSupport, atr, momentum);

            // --------------------------------
            // BUY LOGIC
            // --------------------------------
            if (action == TradeAction.EnterBuy)
            {
                if (nearResistance && !breakingResistance && !strongBreakout)
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Buy blocked: holding resistance | " +
                        $"res={activeResistance:F5}, current={currentPrice:F5}, " +
                        $"touches={resistanceShelf.Touches}, momentum={momentum:F2}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{symbol}] ✅ Buy allowed | " +
                    $"res={activeResistance:F5}, current={currentPrice:F5}, " +
                    $"nearResistance={nearResistance}, breaking={breakingResistance}, " +
                    $"strongBreakout={strongBreakout}, momentum={momentum:F2}"
                );
            }

            // --------------------------------
            // SELL LOGIC
            // --------------------------------
            if (action == TradeAction.EnterSell)
            {
                if (nearSupport && !breakingSupport && !strongBreakout)
                {
                    LogFile.Write(
                        $"[{symbol}] ❌ Sell blocked: holding support | " +
                        $"sup={activeSupport:F5}, current={currentPrice:F5}, " +
                        $"touches={supportShelf.Touches}, momentum={momentum:F2}"
                    );

                    return true;
                }

                LogFile.Write(
                    $"[{symbol}] ✅ Sell allowed | " +
                    $"sup={activeSupport:F5}, current={currentPrice:F5}, " +
                    $"nearSupport={nearSupport}, breaking={breakingSupport}, " +
                    $"strongBreakout={strongBreakout}, momentum={momentum:F2}"
                );
            }

            return false;
        }
    }
}
