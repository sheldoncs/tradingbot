using AutoMapper;
using MetaTrader.Enums;
using MetaTrader.Services.Filter;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Buffers;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MetaTrader.Services
{
    public static class TrendRideContinuationEngine
    {
        private enum AssetProfile
        {
            Gold,
            Fx,
            Index,
            Crypto
        }

        public static double PullbackAtrFactor = 0.5;
        public static double MaxPullbackAtr = 1.2;
        public static double BodyStrength = 0.55;

        public static bool ShouldEnter(
        string symbol,
        MqlRates[] rates,
        double atr,
        double[] emaFast,
        double[] emaSlow,
        double atrPercentile,
        SymbolContext ctx,
        out TradeAction action)
        {
            action = TradeAction.Hold;

            // =============================
            // CLOSED STRUCTURE
            // c0 = latest closed candle
            // c1 = pullback candle
            // c2 = prior trend candle
            // =============================
            var c0 = rates[^2];
            var c1 = rates[^3];
            var c2 = rates[^4];

            var profile = GetProfile(symbol);

            double strongRangeFactor =
               profile == AssetProfile.Gold ? 0.70 :
               profile == AssetProfile.Fx ? 0.60 :
               profile == AssetProfile.Index ? 0.75 :
               0.90;

            double strongBodyRatio =
                profile == AssetProfile.Gold ? 0.50 :
                profile == AssetProfile.Fx ? 0.45 :
                profile == AssetProfile.Index ? 0.50 :
                0.55;

            if (rates == null || rates.Length < 6 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0)
                return false;

            // CLOSED CANDLES
            var last = rates[^2];
            var prev = rates[^3];

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];

            // IMPULSE GUARD
            double impulseRange = last.high - last.low;

            if (impulseRange > atr * 1.8)
                return false;

            // TREND
            bool trendUp =
                emaFast[^2] > emaSlow[^2] &&
                emaFast[^2] > emaFast[^3];

            bool trendDown =
                emaFast[^2] < emaSlow[^2] &&
                emaFast[^2] < emaFast[^3];

            if (!trendUp && !trendDown)
                return false;

            
            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);
            double ratio0 = range0 > 0 ? body0 / range0 : 0.0;

            // =============================
            // TREND DETECTION
            // =============================
            bool bullishTrend =
                emaFast[^2] > emaSlow[^2] &&
                emaFast[^2] > emaFast[^3];

            bool bearishTrend =
                emaFast[^2] < emaSlow[^2] &&
                emaFast[^2] < emaFast[^3];

            // =============================
            // 🔥 ADAPTIVE EMA SLOPE FILTER
            // =============================

            bool strongStructure =
                Math.Abs(emaFast[^1] - emaSlow[^1]) > atr * 0.2;

            bool strongBreakout = true; // trend ride = already in motion

            bool slopeValid = AdaptiveEmaSlopeFilter.IsSlopeValid(
                symbol,
                emaFast,
                emaSlow,
                atr,
                strongStructure,
                strongBreakout
            );

            if (!slopeValid)
            {
                LogFile.Write($"[{symbol}] TrendRide blocked: slope weak");
                return false;
            }

            // ATR REGIME
            if (atrPercentile < 0.55)
                return false;

            // EXTENSION FILTER
            double distanceFromEMA9 = Math.Abs(last.close - ema9);

            if (distanceFromEMA9 > atr * 0.6)
                return false;

            // TREND POSITION
            double distanceFromEMA21 = Math.Abs(last.close - ema21);

            if (distanceFromEMA21 > atr * 2.5)
                return false;

            // PULLBACK DETECTION
            double tolerance = atr * PullbackAtrFactor;

            bool pullbackNearFast =
                prev.low <= emaFast[^3] + tolerance;

            bool pullbackNearFastSell =
                prev.high >= emaFast[^3] - tolerance;

            // PULLBACK DEPTH
            double distToSlow = Math.Abs(prev.close - emaSlow[^3]);

            if (distToSlow > atr * MaxPullbackAtr)
                return false;

            // CANDLE STRENGTH
            double range = last.high - last.low;

            if (range <= 0)
                return false;

            double body = Math.Abs(last.close - last.open);
            double bodyRatio = body / range;

            bool bullishCandle =
                last.close > last.open &&
                bodyRatio >= BodyStrength;

            bool bearishCandle =
                last.close < last.open &&
                bodyRatio >= BodyStrength;

            // BUY
            if (trendUp && pullbackNearFast && bullishCandle)
            {
                Console.WriteLine($"[{symbol}] TrendRide BUY continuation");
                LogFile.Write($"[{symbol}] TrendRide BUY continuation");

                action = TradeAction.EnterBuy;
                return true;
            }

            // SELL
            if (trendDown && pullbackNearFastSell && bearishCandle)
            {
                Console.WriteLine($"[{symbol}] TrendRide SELL continuation");
                LogFile.Write($"[{symbol}] TrendRide SELL continuation");

                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }

        private static AssetProfile GetProfile(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return AssetProfile.Fx;

            symbol = symbol.ToUpperInvariant();

            if (symbol.Contains("XAU"))
                return AssetProfile.Gold;

            if (symbol.Contains("US100") ||
                symbol.Contains("NAS100") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US30") ||
                symbol.Contains("SPX"))
                return AssetProfile.Index;

            if (symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("AVAX") ||
                symbol.Contains("LINK"))
                return AssetProfile.Crypto;

            return AssetProfile.Fx;
        }
    }
}
