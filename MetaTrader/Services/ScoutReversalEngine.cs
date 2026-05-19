using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    using System;

    public static class ScoutReversalEngine
    {
        // --------------------------------
        // PROFILE
        // --------------------------------
        private class ScoutProfile
        {
            public double MinRangeAtr;
            public double BodyRatio;
            public double BreakStrength;
            public double WickRejectionRatio;
            public double MaxDistanceFromEma9Atr;
        }

        private static ScoutProfile GetProfile(string symbol)
        {
            symbol = NormalizeSymbol(symbol);

            if (IsGold(symbol))
                return new ScoutProfile
                {
                    MinRangeAtr = 0.65,
                    BodyRatio = 0.42,
                    BreakStrength = 0.85,
                    WickRejectionRatio = 0.35,
                    MaxDistanceFromEma9Atr = 1.80
                };

            if (IsIndex(symbol))
                return new ScoutProfile
                {
                    MinRangeAtr = 0.75,
                    BodyRatio = 0.45,
                    BreakStrength = 0.95,
                    WickRejectionRatio = 0.35,
                    MaxDistanceFromEma9Atr = 1.90
                };

            if (IsCrypto(symbol))
                return new ScoutProfile
                {
                    MinRangeAtr = 0.65,
                    BodyRatio = 0.40,
                    BreakStrength = 0.80,
                    WickRejectionRatio = 0.32,
                    MaxDistanceFromEma9Atr = 2.10
                };

            // FX default
            return new ScoutProfile
            {
                MinRangeAtr = 0.75,
                BodyRatio = 0.45,
                BreakStrength = 0.95,
                WickRejectionRatio = 0.35,
                MaxDistanceFromEma9Atr = 1.40
            };
        }

        // --------------------------------
        // ENTRY LOGIC
        // --------------------------------
        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            SymbolContext ctx,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (string.IsNullOrWhiteSpace(symbol) ||
                rates == null || rates.Length < 6 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 3 || emaSlow.Length < 3 ||
                ctx == null ||
                atr <= 0)
            {
                return false;
            }

            symbol = NormalizeSymbol(symbol);
            var profile = GetProfile(symbol);

            // --------------------------------
            // CANDLES
            // --------------------------------
            var c0 = rates[^1]; // latest closed / reversal candle
            var c1 = rates[^2]; // previous candle
            var c2 = rates[^3];

            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);

            if (range0 <= 0)
                return false;

            double bodyRatio = body0 / range0;

            double upperWick = c0.high - Math.Max(c0.open, c0.close);
            double lowerWick = Math.Min(c0.open, c0.close) - c0.low;

            double upperWickRatio = upperWick / range0;
            double lowerWickRatio = lowerWick / range0;

            // --------------------------------
            // REGIME
            // Keep Scout selective.
            // It should not fade healthy trend states.
            // --------------------------------
            bool reversalRegime =
                ctx.State == MarketState.Exhaustion ||
                ctx.State == MarketState.LateExpansion;

            if (!reversalRegime)
            {
                LogFile.Write($"[{symbol}] Scout blocked: not reversal regime. State={ctx.State}");
                return false;
            }

            // --------------------------------
            // TREND BEFORE REVERSAL
            // --------------------------------
            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            bool wasDowntrend =
                emaFast[^2] < emaSlow[^2] ||
                c1.close < emaSlow[^2];

            bool wasUptrend =
                emaFast[^2] > emaSlow[^2] ||
                c1.close > emaSlow[^2];

            if (!wasDowntrend && !wasUptrend)
            {
                LogFile.Write($"[{symbol}] Scout blocked: no prior directional trend");
                return false;
            }

            // --------------------------------
            // EXTENSION / LOCATION CHECK
            // Scout reversal needs price stretched enough to justify reversal,
            // but not so chaotic that it is chasing a huge candle.
            // --------------------------------
            double distanceFromEma9 = Math.Abs(c0.close - ema9);

            if (distanceFromEma9 > atr * profile.MaxDistanceFromEma9Atr)
            {
                LogFile.Write(
                    $"[{symbol}] Scout blocked: too far from EMA9 after reversal | " +
                    $"dist={distanceFromEma9:F5}, max={atr * profile.MaxDistanceFromEma9Atr:F5}"
                );
                return false;
            }

            // --------------------------------
            // REVERSAL CANDLE QUALITY
            // --------------------------------
            bool rangeOk =
                range0 >= atr * profile.MinRangeAtr;

            bool bodyOk =
                bodyRatio >= profile.BodyRatio;

            bool bullishClose =
                c0.close > c0.open;

            bool bearishClose =
                c0.close < c0.open;

            bool strongBullish =
                bullishClose &&
                rangeOk &&
                bodyOk;

            bool strongBearish =
                bearishClose &&
                rangeOk &&
                bodyOk;

            // --------------------------------
            // WICK REJECTION
            // Allows reversal entries where the candle rejects a low/high
            // even if the body is not huge.
            // --------------------------------
            bool bullishWickRejection =
                lowerWickRatio >= profile.WickRejectionRatio &&
                c0.close > (c0.low + range0 * 0.55);

            bool bearishWickRejection =
                upperWickRatio >= profile.WickRejectionRatio &&
                c0.close < (c0.high - range0 * 0.55);

            // --------------------------------
            // STRUCTURE BREAK / SWEEP
            // --------------------------------
            double breakBuffer = atr * profile.BreakStrength * 0.15;

            bool breaksUp =
                c0.close > c1.high + breakBuffer;

            bool breaksDown =
                c0.close < c1.low - breakBuffer;

            bool sweptLowAndRecovered =
                c0.low < Math.Min(c1.low, c2.low) - atr * 0.05 &&
                c0.close > c1.close;

            bool sweptHighAndRejected =
                c0.high > Math.Max(c1.high, c2.high) + atr * 0.05 &&
                c0.close < c1.close;

            // --------------------------------
            // MOMENTUM SHIFT
            // --------------------------------
            bool bullishMomentumShift =
                c0.close > c1.close &&
                c0.close > (c0.low + range0 * 0.60);

            bool bearishMomentumShift =
                c0.close < c1.close &&
                c0.close < (c0.high - range0 * 0.60);

            // --------------------------------
            // BUY SCOUT
            // --------------------------------
            bool buy =
                wasDowntrend &&
                (
                    strongBullish ||
                    bullishWickRejection
                ) &&
                (
                    breaksUp ||
                    sweptLowAndRecovered ||
                    bullishMomentumShift
                );

            if (buy)
            {
                LogFile.Write(
                    $"[{symbol}] 🟢 Scout BUY | " +
                    $"rangeAtr={(range0 / atr):F2}, bodyRatio={bodyRatio:F2}, " +
                    $"lowerWick={lowerWickRatio:F2}, breakUp={breaksUp}, sweep={sweptLowAndRecovered}"
                );

                action = TradeAction.EnterBuy;
                return true;
            }

            // --------------------------------
            // SELL SCOUT
            // --------------------------------
            bool sell =
                wasUptrend &&
                (
                    strongBearish ||
                    bearishWickRejection
                ) &&
                (
                    breaksDown ||
                    sweptHighAndRejected ||
                    bearishMomentumShift
                );

            if (sell)
            {
                LogFile.Write(
                    $"[{symbol}] 🔴 Scout SELL | " +
                    $"rangeAtr={(range0 / atr):F2}, bodyRatio={bodyRatio:F2}, " +
                    $"upperWick={upperWickRatio:F2}, breakDown={breaksDown}, sweep={sweptHighAndRejected}"
                );

                action = TradeAction.EnterSell;
                return true;
            }

            LogFile.Write(
                $"[{symbol}] Scout blocked: no reversal trigger | " +
                $"wasDown={wasDowntrend}, wasUp={wasUptrend}, " +
                $"strongBull={strongBullish}, strongBear={strongBearish}, " +
                $"bullWick={bullishWickRejection}, bearWick={bearishWickRejection}, " +
                $"breakUp={breaksUp}, breakDown={breaksDown}"
            );

            return false;
        }

        // --------------------------------
        // SYMBOL HELPERS
        // --------------------------------
        private static string NormalizeSymbol(string s)
        {
            return string.IsNullOrWhiteSpace(s)
                ? ""
                : s.ToUpperInvariant();
        }

        private static bool IsGold(string s)
        {
            s = NormalizeSymbol(s);

            return s.Contains("XAU") ||
                   s.Contains("XAG") ||
                   s.Contains("GOLD") ||
                   s.Contains("SILVER");
        }

        private static bool IsIndex(string s)
        {
            s = NormalizeSymbol(s);

            return s.Contains("US100") ||
                   s.Contains("NAS") ||
                   s.Contains("USTEC") ||
                   s.Contains("US30") ||
                   s.Contains("DJ") ||
                   s.Contains("SPX") ||
                   s.Contains("US500") ||
                   s.Contains("DAX") ||
                   s.Contains("GER") ||
                   s.Contains("UK100");
        }

        private static bool IsCrypto(string s)
        {
            s = NormalizeSymbol(s);

            return s.Contains("BTC") ||
                   s.Contains("ETH") ||
                   s.Contains("SOL") ||
                   s.Contains("AVAX") ||
                   s.Contains("BNB") ||
                   s.Contains("XRP") ||
                   s.Contains("ADA") ||
                   s.Contains("DOGE") ||
                   s.Contains("LTC") ||
                   s.Contains("LINK");
        }
    }
}
