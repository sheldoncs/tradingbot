using MetaTrader.Enums;
using MetaTrader.Services.Filter;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public static class EmaPullbackContinuationEngine
    {
        private enum AssetProfile
        {
            Gold,
            Fx,
            Index,
            Crypto
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

        public static bool ShouldEnter(
            string symbol,
            MtApi5Client client,
            MqlRates[] rates,
            double atr,
            double[] emaFast,
            double[] emaSlow,
            double momentum,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (rates == null || rates.Length < 6 ||
                emaFast == null || emaSlow == null ||
                emaFast.Length < 5 || emaSlow.Length < 5 ||
                atr <= 0 ||
                client == null)
                return false;

            var profile = GetProfile(symbol);

            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            var c0 = rates[^2]; // latest closed
            var c1 = rates[^3]; // pullback

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];

            // =============================
            // PROFILE SETTINGS (UPDATED)
            // =============================
            double maxDistanceFromEma9Atr =
                profile == AssetProfile.Gold ? 1.6 :
                profile == AssetProfile.Fx ? 1.2 :
                profile == AssetProfile.Index ? 1.5 :
                1.8;

            double maxDistanceFromEma21Atr =
                profile == AssetProfile.Gold ? 3.0 :
                profile == AssetProfile.Fx ? 2.2 :
                profile == AssetProfile.Index ? 2.8 :
                3.2;

            double pullbackToleranceAtr =
                profile == AssetProfile.Gold ? 0.60 :
                profile == AssetProfile.Fx ? 0.45 :
                profile == AssetProfile.Index ? 0.55 :
                0.75;

            double pullbackBodyLimitAtr =
                profile == AssetProfile.Gold ? 0.80 :
                profile == AssetProfile.Fx ? 0.60 :
                profile == AssetProfile.Index ? 0.70 :
                0.90;

            double minMomentum =
                profile == AssetProfile.Gold ? 3.0 :
                profile == AssetProfile.Fx ? 3.2 :
                profile == AssetProfile.Index ? 3.2 :
                3.0;

            // =============================
            // TREND
            // =============================
            bool bullishTrend =
                emaFast[^2] > emaSlow[^2] &&
                emaFast[^2] > emaFast[^3];

            bool bearishTrend =
                emaFast[^2] < emaSlow[^2] &&
                emaFast[^2] < emaFast[^3];

            if (!bullishTrend && !bearishTrend)
                return false;

            // =============================
            // EXTENSION (RELAXED)
            // =============================
            double distBuyEma9 = Math.Abs(ask - ema9);
            double distSellEma9 = Math.Abs(bid - ema9);

            if (distBuyEma9 > atr * maxDistanceFromEma9Atr &&
                distSellEma9 > atr * maxDistanceFromEma9Atr)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: too extended EMA9");
                return false;
            }

            double distBuyEma21 = Math.Abs(ask - ema21);
            double distSellEma21 = Math.Abs(bid - ema21);

            if (distBuyEma21 > atr * maxDistanceFromEma21Atr &&
                distSellEma21 > atr * maxDistanceFromEma21Atr)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: too extended EMA21");
                return false;
            }

            // =============================
            // PULLBACK QUALITY
            // =============================
            double c1Body = Math.Abs(c1.close - c1.open);

            bool healthyPullback = c1Body < atr * pullbackBodyLimitAtr;

            if (!healthyPullback)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: unhealthy");
                return false;
            }

            // =============================
            // EMA PROXIMITY (RELAXED + SHALLOW)
            // =============================
            bool nearEma =
                Math.Abs(c1.close - emaFast[^3]) <= atr * pullbackToleranceAtr;

            bool shallowPullback =
                Math.Abs(c1.close - emaFast[^3]) <= atr * 1.2;

            bool validPullback = nearEma || shallowPullback;

            // =============================
            // MOMENTUM (ADAPTIVE)
            // =============================
            bool earlyPhase = c1Body < atr * 0.5;

            bool momentumOk =
                earlyPhase
                    ? momentum >= minMomentum * 0.65
                    : momentum >= minMomentum * 0.85;

            // =============================
            // CONTINUATION STRUCTURE
            // =============================
            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);

            if (range0 <= 0) return false;

            double bodyRatio = body0 / range0;

            bool basicContinuation =
                bodyRatio >= 0.35;

            // =============================
            // MOMENTUM REBUILD
            // =============================
            double prevMove = Math.Abs(c1.close - c1.open);
            double moveBuy = Math.Abs(ask - c0.open);
            double moveSell = Math.Abs(bid - c0.open);

            bool momentumRebuildBuy = moveBuy > prevMove * 0.6;
            bool momentumRebuildSell = moveSell > prevMove * 0.6;

            bool acceptableContinuation =
                basicContinuation || momentumRebuildBuy || momentumRebuildSell;

            // =============================
            // BREAK (ENTRY TRIGGER)
            // =============================
            bool breakBuy = ask > c1.high;
            bool breakSell = bid < c1.low;

            // =============================
            // ENTRY
            // =============================
            if (bullishTrend &&
                validPullback &&
                acceptableContinuation &&
                momentumOk &&
                breakBuy)
            {
                LogFile.Write($"[{symbol}] Pullback BUY");
                action = TradeAction.EnterBuy;
                return true;
            }

            if (bearishTrend &&
                validPullback &&
                acceptableContinuation &&
                momentumOk &&
                breakSell)
            {
                LogFile.Write($"[{symbol}] Pullback SELL");
                action = TradeAction.EnterSell;
                return true;
            }

            return false;
        }
    }
}
