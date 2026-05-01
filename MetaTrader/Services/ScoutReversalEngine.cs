using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
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
        }

        private static ScoutProfile GetProfile(string symbol)
        {
            if (IsGold(symbol))
                return new ScoutProfile
                {
                    MinRangeAtr = 0.75,
                    BodyRatio = 0.50,
                    BreakStrength = 1.0
                };

            if (IsIndex(symbol))
                return new ScoutProfile
                {
                    MinRangeAtr = 0.9,
                    BodyRatio = 0.55,
                    BreakStrength = 1.1
                };

            if (IsCrypto(symbol))
                return new ScoutProfile
                {
                    MinRangeAtr = 0.7,
                    BodyRatio = 0.45,
                    BreakStrength = 0.9
                };

            // FX default
            return new ScoutProfile
            {
                MinRangeAtr = 1.0,
                BodyRatio = 0.6,
                BreakStrength = 1.2
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

            if (rates == null || rates.Length < 5 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 3 || emaSlow.Length < 3 ||
                atr <= 0)
                return false;

            var profile = GetProfile(symbol);

            // --------------------------------
            // CANDLES
            // --------------------------------
            var c0 = rates[^1];
            var c1 = rates[^2];

            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);

            if (range0 <= 0)
                return false;

            double bodyRatio = body0 / range0;

            // --------------------------------
            // REGIME (RELAXED)
            // --------------------------------
            if (ctx.State != MarketState.Exhaustion &&
                ctx.State != MarketState.LateExpansion)
                return false;

            // --------------------------------
            // TREND BEFORE REVERSAL
            // --------------------------------
            bool wasDowntrend = emaFast[^2] < emaSlow[^2];
            bool wasUptrend = emaFast[^2] > emaSlow[^2];

            // --------------------------------
            // STRONG REVERSAL CANDLE (RELAXED)
            // --------------------------------
            bool strongBullish =
                c0.close > c0.open &&
                range0 >= atr * (profile.MinRangeAtr * 0.85) &&
                bodyRatio >= (profile.BodyRatio * 0.85);

            bool strongBearish =
                c0.close < c0.open &&
                range0 >= atr * (profile.MinRangeAtr * 0.85) &&
                bodyRatio >= (profile.BodyRatio * 0.85);

            // --------------------------------
            // STRUCTURE BREAK (FIXED LOGIC)
            // --------------------------------
            bool breaksUp =
                c0.close > c1.high + (atr * profile.BreakStrength * 0.2);

            bool breaksDown =
                c0.close < c1.low - (atr * profile.BreakStrength * 0.2);

            // --------------------------------
            // BUY SCOUT
            // --------------------------------
            if (wasDowntrend && strongBullish && (breaksUp || bodyRatio > 0.7))
            {
                LogFile.Write($"[{symbol}] 🟢 Scout BUY (adaptive)");
                action = TradeAction.EnterBuy;
                return true;
            }

            // --------------------------------
            // SELL SCOUT
            // --------------------------------
            if (wasUptrend && strongBearish && (breaksDown || bodyRatio > 0.7))
            {
                LogFile.Write($"[{symbol}] 🔴 Scout SELL (adaptive)");
                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }

        // --------------------------------
        // SYMBOL HELPERS
        // --------------------------------
        private static bool IsGold(string s) =>
            s.Contains("XAU");

        private static bool IsIndex(string s) =>
            s.Contains("US100") || s.Contains("NAS") || s.Contains("SPX");

        private static bool IsCrypto(string s) =>
            s.Contains("BTC") || s.Contains("ETH") || s.Contains("SOL") || s.Contains("AVAX") || s.Contains("BNB");
    }
}
