using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;

namespace MetaTrader.Services
{
    public static class OrderBlockFvgEngine
    {
        private class Profile
        {
            public double MinImpulseAtr;
            public double MinFvgAtr;
            public double ZoneToleranceAtr;
        }

        private static Profile GetProfile(string symbol)
        {
            if (symbol.Contains("XAU"))
            {
                return new Profile
                {
                    MinImpulseAtr = 1.2,
                    MinFvgAtr = 0.4,
                    ZoneToleranceAtr = 0.15
                };
            }

            if (symbol.Contains("USD") && (symbol.Contains("ETH") || symbol.Contains("BTC") || symbol.Contains("SOL") || symbol.Contains("AVAX") || symbol.Contains("BNB")))
            {
                return new Profile
                {
                    MinImpulseAtr = 1.5,
                    MinFvgAtr = 0.5,
                    ZoneToleranceAtr = 0.20
                };
            }

            if (symbol.Contains("US100") || symbol.Contains("NAS") || symbol.Contains("SPX"))
            {
                return new Profile
                {
                    MinImpulseAtr = 1.6,
                    MinFvgAtr = 0.5,
                    ZoneToleranceAtr = 0.20
                };
            }

            // Default = FX
            return new Profile
            {
                MinImpulseAtr = 1.0,
                MinFvgAtr = 0.3,
                ZoneToleranceAtr = 0.10
            };
        }

        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < 6 || atr <= 0)
                return false;

            var profile = GetProfile(symbol);

            var c0 = rates[^1]; // current closed
            var c1 = rates[^2]; // impulse candle
            var c2 = rates[^3]; // OB candle

            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            bool trendUp = ema9 > ema21;
            bool trendDown = ema9 < ema21;

            // --------------------------------
            // IMPULSE
            // --------------------------------
            double impulse = c1.high - c1.low;

            if (impulse < atr * profile.MinImpulseAtr)
                return false;

            // --------------------------------
            // FVG DETECTION (with tolerance)
            // --------------------------------
            double fvgTolerance = atr * 0.05;

            bool bullishFvg =
                c2.high <= c0.low + fvgTolerance &&
                (c0.low - c2.high) > atr * profile.MinFvgAtr;

            bool bearishFvg =
                c2.low >= c0.high - fvgTolerance &&
                (c2.low - c0.high) > atr * profile.MinFvgAtr;

            // --------------------------------
            // ORDER BLOCK
            // --------------------------------
            bool bullishOB = c2.close < c2.open;
            bool bearishOB = c2.close > c2.open;

            // --------------------------------
            // RETURN INTO ZONE (with ATR tolerance)
            // --------------------------------
            double zoneTol = atr * profile.ZoneToleranceAtr;

            bool inBullZone =
                c0.low <= c1.low + zoneTol &&
                c0.low >= c2.high - zoneTol;

            bool inBearZone =
                c0.high >= c1.high - zoneTol &&
                c0.high <= c2.low + zoneTol;

            // --------------------------------
            // FINAL CONDITIONS
            // --------------------------------
            bool buy =
                trendUp &&
                bullishFvg &&
                bullishOB &&
                inBullZone;

            bool sell =
                trendDown &&
                bearishFvg &&
                bearishOB &&
                inBearZone;

            if (buy)
            {
                LogFile.Write($"[{symbol}] ✅ OB/FVG BUY");
                action = TradeAction.EnterBuy;
                return true;
            }

            if (sell)
            {
                LogFile.Write($"[{symbol}] ✅ OB/FVG SELL");
                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }
    }
}
