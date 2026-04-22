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

            // =============================
            // LIVE PRICE
            // =============================
            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            // =============================
            // CLOSED STRUCTURE
            // c0 = latest closed candle
            // c1 = pullback candle
            // c2 = prior trend candle
            // =============================
            var c0 = rates[^2];
            var c1 = rates[^3];
            var c2 = rates[^4];

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];

            // =============================
            // PROFILE SETTINGS
            // =============================
            double impulseCooldownFactor =
                profile == AssetProfile.Gold ? 2.0 :
                profile == AssetProfile.Fx ? 1.6 :
                profile == AssetProfile.Index ? 1.9 :
                2.2; // crypto

            double minSlopeFactor =
                profile == AssetProfile.Gold ? 0.30 :
                profile == AssetProfile.Fx ? 0.40 :
                profile == AssetProfile.Index ? 0.30 :
                0.25;

            double maxDistanceFromEma9Atr =
                profile == AssetProfile.Gold ? 1.2 :
                profile == AssetProfile.Fx ? 1.0 :
                profile == AssetProfile.Index ? 1.2 :
                1.4;

            double maxDistanceFromEma21Atr =
                profile == AssetProfile.Gold ? 2.8 :
                profile == AssetProfile.Fx ? 2.0 :
                profile == AssetProfile.Index ? 2.6 :
                3.0;

            double pullbackToleranceAtr =
                profile == AssetProfile.Gold ? 0.60 :
                profile == AssetProfile.Fx ? 0.40 :
                profile == AssetProfile.Index ? 0.50 :
                0.70;

            double pullbackBodyLimitAtr =
                profile == AssetProfile.Gold ? 0.70 :
                profile == AssetProfile.Fx ? 0.50 :
                profile == AssetProfile.Index ? 0.60 :
                0.80;

            double momentumRebuildFactor =
                profile == AssetProfile.Gold ? 0.60 :
                profile == AssetProfile.Fx ? 0.70 :
                profile == AssetProfile.Index ? 0.65 :
                0.50;

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

            double earlyRangeFactor =
                profile == AssetProfile.Gold ? 0.40 :
                profile == AssetProfile.Fx ? 0.35 :
                profile == AssetProfile.Index ? 0.45 :
                0.50;

            double earlyBodyRatio =
                profile == AssetProfile.Gold ? 0.30 :
                profile == AssetProfile.Fx ? 0.28 :
                profile == AssetProfile.Index ? 0.30 :
                0.35;

            double minMomentum =
                profile == AssetProfile.Gold ? 3.0 :
                profile == AssetProfile.Fx ? 3.2 :
                profile == AssetProfile.Index ? 3.2 :
                3.0;

            double earlyMomentum =
                profile == AssetProfile.Gold ? 3.8 :
                profile == AssetProfile.Fx ? 4.0 :
                profile == AssetProfile.Index ? 4.0 :
                4.0;

            // =============================
            // IMPULSE COOLDOWN GUARD
            // Block obvious exhaustion candle
            // =============================
            double impulseRange = c0.high - c0.low;
            if (impulseRange > atr * impulseCooldownFactor)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: latest candle too impulsive");
                return false;
            }

            // =============================
            // TREND DETECTION
            // =============================
            bool bullishTrend =
                emaFast[^2] > emaSlow[^2] &&
                emaFast[^2] > emaFast[^3];

            bool bearishTrend =
                emaFast[^2] < emaSlow[^2] &&
                emaFast[^2] < emaFast[^3];

            if (!bullishTrend && !bearishTrend)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: no clear trend");
                return false;
            }

            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);
            double ratio0 = range0 > 0 ? body0 / range0 : 0.0;

            // =============================
            // 🔥 ADAPTIVE EMA SLOPE FILTER (UPGRADED)
            // =============================

            // Determine structure strength (directional context)
            bool strongStructure =
                (bullishTrend && emaFast[^2] > emaSlow[^2]) ||
                (bearishTrend && emaFast[^2] < emaSlow[^2]);

            // Determine breakout / continuation strength
            bool strongBreakout =
               range0 > atr * strongRangeFactor &&
                ratio0 > strongBodyRatio;

            // Apply adaptive slope logic
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
                LogFile.Write($"[{symbol}] Pullback blocked: adaptive slope");
                return false;
            }
            // =============================
            // EXTENSION FILTER
            // Use LIVE price, not just closed candle
            // =============================
            double distBuyEma9 = Math.Abs(ask - ema9);
            double distSellEma9 = Math.Abs(bid - ema9);

            if (distBuyEma9 > atr * maxDistanceFromEma9Atr &&
                distSellEma9 > atr * maxDistanceFromEma9Atr)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: too extended from EMA9");
                return false;
            }

            double distBuyEma21 = Math.Abs(ask - ema21);
            double distSellEma21 = Math.Abs(bid - ema21);

            if (distBuyEma21 > atr * maxDistanceFromEma21Atr &&
                distSellEma21 > atr * maxDistanceFromEma21Atr)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: too extended from EMA21");
                return false;
            }

            // =============================
            // CONTINUATION CANDLE QUALITY
            // =============================
           

            bool strongContinuation =
                range0 > atr * strongRangeFactor &&
                ratio0 > strongBodyRatio;

            bool earlyContinuation =
                range0 > atr * earlyRangeFactor &&
                ratio0 > earlyBodyRatio &&
                momentum >= earlyMomentum;

            if (!strongContinuation && !earlyContinuation)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: weak continuation candle");
                return false;
            }

            // =============================
            // 🔥 MOMENTUM USAGE (FIXED)
            // Allow low momentum during pullback
            // Require sufficient momentum ONLY for entry
            // =============================

            bool momentumOkForEntry = momentum >= minMomentum * 0.85;

            // Debug visibility (optional)
            if (!momentumOkForEntry)
            {
                LogFile.Write($"[{symbol}] Pullback note: momentum low but allowed ({momentum:F2})");
            }

            // =============================
            // PULLBACK HEALTH
            // =============================
            double c1Body = Math.Abs(c1.close - c1.open);

            bool healthyPullback = c1Body < atr * pullbackBodyLimitAtr;

            if (!healthyPullback)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: unhealthy pullback body");
                return false;
            }

            // =============================
            // EMA PROXIMITY OF PULLBACK
            // relaxed from exact-touch logic
            // =============================
            bool nearEmaBuy =
                Math.Abs(c1.low - emaFast[^3]) <= atr * pullbackToleranceAtr ||
                (c1.low <= emaFast[^3] + atr * pullbackToleranceAtr &&
                 c1.low >= emaFast[^3] - atr * pullbackToleranceAtr);

            bool nearEmaSell =
                Math.Abs(c1.high - emaFast[^3]) <= atr * pullbackToleranceAtr ||
                (c1.high <= emaFast[^3] + atr * pullbackToleranceAtr &&
                 c1.high >= emaFast[^3] - atr * pullbackToleranceAtr);

            // =============================
            // MOMENTUM REBUILD
            // key improvement:
            // allow re-acceleration instead of requiring perfect candle
            // =============================
            double prevMove = Math.Abs(c1.close - c1.open);
            double moveBuy = Math.Abs(ask - c0.open);
            double moveSell = Math.Abs(bid - c0.open);

            bool momentumRebuildBuy = moveBuy > prevMove * momentumRebuildFactor;
            bool momentumRebuildSell = moveSell > prevMove * momentumRebuildFactor;

            // =============================
            // LIVE CONTINUATION TRIGGER
            // allows earlier entry than waiting for perfect close
            // =============================
            bool breakBuy =
                ask > c1.high ||
                ask > c0.high;

            bool breakSell =
                bid < c1.low ||
                bid < c0.low;

            // =============================
            // BUY SETUP
            // =============================
            if (bullishTrend)
            {
                if (nearEmaBuy &&
                    healthyPullback &&
                    breakBuy &&
                    momentumRebuildBuy &&
                    momentumOkForEntry)
                {
                    LogFile.Write($"[{symbol}] Pullback BUY signal: momentum={momentum:F2}, moveBuy={moveBuy:F5}, prevMove={prevMove:F5}"); 
                    action = TradeAction.EnterBuy;
                    return true;
                }
            }

            // =============================
            // SELL SETUP
            // =============================
            if (bearishTrend)
            {
                if (nearEmaSell &&
                    healthyPullback &&
                    breakSell &&
                    momentumRebuildSell &&
                    momentumOkForEntry)
                {
                    LogFile.Write($"[{symbol}] Pullback SELL signal: momentum={momentum:F2}, moveSell={moveSell:F5}, prevMove={prevMove:F5}");
                    action = TradeAction.EnterSell;
                    return true;
                }
            }

            return false;
        }
    }
}
