using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Linq;

namespace MetaTrader.Services
{
    public static class ExplosionEntryEngine
    {
        // ---------------------------------------------
        // PROFILE
        // ---------------------------------------------
        private sealed class ExplosionProfile
        {
            public double MinRangeAtr { get; set; }
            public double MinBodyRatio { get; set; }
            public double MinBreakAtr { get; set; }
            public double MaxEmaDistanceAtr { get; set; }
            public double MinEmaSeparationAtr { get; set; }
            public double MaxSpreadAtrFactor { get; set; }
        }

        private static ExplosionProfile GetProfile(string symbol)
        {
            if (IsGold(symbol))
            {
                return new ExplosionProfile
                {
                    MinRangeAtr = 1.80,
                    MinBodyRatio = 0.70,
                    MinBreakAtr = 0.20,
                    MaxEmaDistanceAtr = 1.20,
                    MinEmaSeparationAtr = 0.15,
                    MaxSpreadAtrFactor = 0.08
                };
            }

            if (IsIndex(symbol))
            {
                return new ExplosionProfile
                {
                    MinRangeAtr = 1.60,
                    MinBodyRatio = 0.68,
                    MinBreakAtr = 0.18,
                    MaxEmaDistanceAtr = 1.10,
                    MinEmaSeparationAtr = 0.12,
                    MaxSpreadAtrFactor = 0.10
                };
            }

            if (IsCrypto(symbol))
            {
                return new ExplosionProfile
                {
                    MinRangeAtr = 1.90,
                    MinBodyRatio = 0.72,
                    MinBreakAtr = 0.22,
                    MaxEmaDistanceAtr = 1.40,
                    MinEmaSeparationAtr = 0.10,
                    MaxSpreadAtrFactor = 0.12
                };
            }

            // FX default
            return new ExplosionProfile
            {
                MinRangeAtr = 1.70,
                MinBodyRatio = 0.70,
                MinBreakAtr = 0.15,
                MaxEmaDistanceAtr = 1.00,
                MinEmaSeparationAtr = 0.10,
                MaxSpreadAtrFactor = 0.06
            };
        }

        // ---------------------------------------------
        // MAIN ENTRY
        // ---------------------------------------------
        public static bool ShouldEnter(
            string symbol,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double bid,
            double ask,
            double point,
            out TradeAction action)
        {
            action = TradeAction.Hold;
            
            if (string.IsNullOrWhiteSpace(symbol) ||
                rates == null || rates.Length < 12 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0 || point <= 0 ||
                bid <= 0 || ask <= 0)
                return false;

            var profile = GetProfile(symbol);

            var c0 = rates[^1];
            var c1 = rates[^2];
            var c2 = rates[^3];

            double range0 = c0.high - c0.low;
            if (range0 <= 0)
                return false;

            double body0 = Math.Abs(c0.close - c0.open);
            double bodyRatio0 = body0 / range0;

            // ---------------------------------------------
            // LIVE PRICE + SPREAD
            // ---------------------------------------------
            double liveMid = (bid + ask) / 2.0;
            double spread = Math.Abs(ask - bid);

            if (spread > atr * profile.MaxSpreadAtrFactor)
            {
                LogFile.Write($"[{symbol}] Explosion blocked: spread too wide ({spread:F5})");
                return false;
            }

            // ---------------------------------------------
            // EXPLOSION CANDLE TEST
            // ---------------------------------------------
            bool explosiveRange = range0 >= atr * profile.MinRangeAtr;
            bool strongBody = bodyRatio0 >= profile.MinBodyRatio;

            if (!explosiveRange || !strongBody)
            {
                LogFile.Write($"[{symbol}] Explosion blocked: candle not explosive enough");
                return false;
            }

            // ---------------------------------------------
            // RECENT STRUCTURE LEVELS
            // exclude c0 and c1 to avoid ultra-short lookback noise
            // ---------------------------------------------
            double recentHigh = rates.Skip(rates.Length - 10).Take(8).Max(x => x.high);
            double recentLow = rates.Skip(rates.Length - 10).Take(8).Min(x => x.low);

            bool breaksUp = c0.close > recentHigh + (atr * profile.MinBreakAtr);
            bool breaksDown = c0.close < recentLow - (atr * profile.MinBreakAtr);

            if (!breaksUp && !breaksDown)
            {
                LogFile.Write($"[{symbol}] Explosion blocked: no structure break");
                return false;
            }

            // ---------------------------------------------
            // EMA CONTEXT
            // do not take pure random spikes
            // ---------------------------------------------
            double emaSepNow = Math.Abs(emaFast[^1] - emaSlow[^1]);
            bool emaContextValid = emaSepNow >= atr * profile.MinEmaSeparationAtr;

            if (!emaContextValid)
            {
                LogFile.Write($"[{symbol}] Explosion blocked: EMA context too weak");
                return false;
            }

            // ---------------------------------------------
            // EXTENSION GUARD
            // if already absurdly far from EMA, skip
            // ---------------------------------------------
            double distanceFromFastEma = Math.Abs(liveMid - emaFast[^1]);

            if (distanceFromFastEma > atr * profile.MaxEmaDistanceAtr)
            {
                LogFile.Write($"[{symbol}] Explosion blocked: too far from EMA");
                return false;
            }

            // ---------------------------------------------
            // SHOCK / WHIPSAW GUARD
            // reject if previous candle was already huge opposite noise
            // ---------------------------------------------
            double range1 = c1.high - c1.low;
            bool prevHuge = range1 >= atr * (profile.MinRangeAtr * 0.9);

            bool bullishExplosion =
                c0.close > c0.open &&
                breaksUp &&
                emaFast[^1] >= emaSlow[^1];

            bool bearishExplosion =
                c0.close < c0.open &&
                breaksDown &&
                emaFast[^1] <= emaSlow[^1];

            // prevent immediate whipsaw after giant opposite candle
            if (prevHuge)
            {
                bool oppositePrevBull = c1.close > c1.open;
                bool oppositePrevBear = c1.close < c1.open;

                if ((bullishExplosion && oppositePrevBear) ||
                    (bearishExplosion && oppositePrevBull))
                {
                    LogFile.Write($"[{symbol}] Explosion blocked: prior candle too unstable");
                    return false;
                }
            }

            // ---------------------------------------------
            // FOLLOW-THROUGH HINT
            // prefer when c0 is larger than c1 and c2
            // ---------------------------------------------
            double range2 = c2.high - c2.low;

            bool dominantExpansion =
                range0 >= range1 * 1.10 &&
                range0 >= range2 * 1.10;

            if (!dominantExpansion)
            {
                LogFile.Write($"[{symbol}] Explosion blocked: weak dominance vs prior candles");
                return false;
            }

            // ---------------------------------------------
            // FINAL DECISION
            // ---------------------------------------------
            if (bullishExplosion)
            {
                LogFile.Write($"[{symbol}] 💥 Explosion BUY");
                action = TradeAction.EnterBuy;
                return true;
            }

            if (bearishExplosion)
            {
                LogFile.Write($"[{symbol}] 💥 Explosion SELL");
                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }

        // ---------------------------------------------
        // OPTIONAL: QUICK EXIT HELPER
        // Use this only for explosion trades if you want
        // faster management than normal trades.
        // ---------------------------------------------
        public static bool ShouldForceExit(
            TrackedTrade trade,
            string symbol,
            double atr,
            double bid,
            double ask,
            double[] emaFast)
        {
            if (trade == null || atr <= 0 || emaFast == null || emaFast.Length == 0)
                return false;

            double livePrice = trade.Side == TradeSide.Buy ? bid : ask;
            double fastEma = emaFast[^1];

            // Exit if price snaps back across EMA9 quickly
            if (trade.Side == TradeSide.Buy && livePrice < fastEma)
            {
                LogFile.Write($"[{symbol}] Explosion exit: buy lost EMA9");
                return true;
            }

            if (trade.Side == TradeSide.Sell && livePrice > fastEma)
            {
                LogFile.Write($"[{symbol}] Explosion exit: sell lost EMA9");
                return true;
            }
            
            // Exit if trade had some profit but gives back too much
            if (trade.MaxProfitUsd >= 2.0 && trade.ProfitUsd <= trade.MaxProfitUsd * 0.35)
            {
                LogFile.Write($"[{symbol}] Explosion exit: profit giveback");
                return true;
            }

            return false;
        }

        // ---------------------------------------------
        // HELPERS
        // ---------------------------------------------
        private static bool IsGold(string s) =>
            !string.IsNullOrWhiteSpace(s) && s.Contains("XAU", StringComparison.OrdinalIgnoreCase);

        private static bool IsIndex(string s) =>
            !string.IsNullOrWhiteSpace(s) &&
            (s.Contains("US100", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("NAS", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("SPX", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("DJI", StringComparison.OrdinalIgnoreCase));

        private static bool IsCrypto(string s) =>
            !string.IsNullOrWhiteSpace(s) &&
            (s.Contains("BTC", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("ETH", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("SOL", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("BNB", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("XRP", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("ADA", StringComparison.OrdinalIgnoreCase));
    }
}
