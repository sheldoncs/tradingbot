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

            if (symbol.Contains("XAU") ||
                symbol.Contains("XAG") ||
                symbol.Contains("GOLD") ||
                symbol.Contains("SILVER"))
                return AssetProfile.Gold;

            if (symbol.Contains("US100") ||
                symbol.Contains("NAS100") ||
                symbol.Contains("USTEC") ||
                symbol.Contains("US30") ||
                symbol.Contains("SPX") ||
                symbol.Contains("US500") ||
                symbol.Contains("DAX") ||
                symbol.Contains("GER") ||
                symbol.Contains("UK100"))
                return AssetProfile.Index;

            if (symbol.Contains("BTC") ||
                symbol.Contains("ETH") ||
                symbol.Contains("SOL") ||
                symbol.Contains("BNB") ||
                symbol.Contains("XRP") ||
                symbol.Contains("ADA") ||
                symbol.Contains("AVAX") ||
                symbol.Contains("LINK") ||
                symbol.Contains("DOGE") ||
                symbol.Contains("LTC"))
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

            if (bid <= 0 || ask <= 0 || ask <= bid)
            {
                LogFile.Write($"[{symbol}] Pullback blocked: invalid bid/ask");
                return false;
            }

            var c0 = rates[^2]; // latest closed continuation candle
            var c1 = rates[^3]; // pullback candle

            double ema9 = emaFast[^2];
            double ema21 = emaSlow[^2];

            double ema9Prev = emaFast[^3];
            double ema21Prev = emaSlow[^3];

            // =============================
            // PROFILE SETTINGS
            // =============================
            double maxDistanceFromEma9Atr =
                profile == AssetProfile.Gold ? 1.45 :
                profile == AssetProfile.Fx ? 1.05 :
                profile == AssetProfile.Index ? 1.30 :
                1.65; // Crypto

            double maxDistanceFromEma21Atr =
                profile == AssetProfile.Gold ? 2.80 :
                profile == AssetProfile.Fx ? 2.00 :
                profile == AssetProfile.Index ? 2.60 :
                3.00; // Crypto

            double pullbackToleranceAtr =
                profile == AssetProfile.Gold ? 0.70 :
                profile == AssetProfile.Fx ? 0.55 :
                profile == AssetProfile.Index ? 0.65 :
                0.85; // Crypto

            double pullbackBodyLimitAtr =
                profile == AssetProfile.Gold ? 0.90 :
                profile == AssetProfile.Fx ? 0.70 :
                profile == AssetProfile.Index ? 0.80 :
                1.00; // Crypto

            double minMomentum =
                profile == AssetProfile.Gold ? 2.8 :
                profile == AssetProfile.Fx ? 2.9 :
                profile == AssetProfile.Index ? 3.0 :
                2.8; // Crypto

            double continuationBodyRatioRequired =
                profile == AssetProfile.Fx ? 0.32 :
                profile == AssetProfile.Gold ? 0.35 :
                profile == AssetProfile.Index ? 0.34 :
                0.33;

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
            {
                LogFile.Write($"[{symbol}] Pullback blocked: no clear trend");
                return false;
            }

            double emaSlope = emaFast[^2] - emaFast[^3];
            bool strongTrend = Math.Abs(emaSlope) > atr * 0.05;

            // =============================
            // DIRECTIONAL EXTENSION CHECK
            // =============================
            double distBuyEma9 = Math.Abs(ask - ema9);
            double distSellEma9 = Math.Abs(bid - ema9);

            double distBuyEma21 = Math.Abs(ask - ema21);
            double distSellEma21 = Math.Abs(bid - ema21);

            if (bullishTrend && !strongTrend && distBuyEma9 > atr * maxDistanceFromEma9Atr)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback BUY blocked: too extended from EMA9 | " +
                    $"dist={distBuyEma9:F5}, max={atr * maxDistanceFromEma9Atr:F5}"
                );
                return false;
            }

            if (bearishTrend && !strongTrend && distSellEma9 > atr * maxDistanceFromEma9Atr)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback SELL blocked: too extended from EMA9 | " +
                    $"dist={distSellEma9:F5}, max={atr * maxDistanceFromEma9Atr:F5}"
                );
                return false;
            }

            if (bullishTrend && distBuyEma21 > atr * maxDistanceFromEma21Atr)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback BUY blocked: too extended from EMA21 | " +
                    $"dist={distBuyEma21:F5}, max={atr * maxDistanceFromEma21Atr:F5}"
                );
                return false;
            }

            if (bearishTrend && distSellEma21 > atr * maxDistanceFromEma21Atr)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback SELL blocked: too extended from EMA21 | " +
                    $"dist={distSellEma21:F5}, max={atr * maxDistanceFromEma21Atr:F5}"
                );
                return false;
            }

            // =============================
            // PULLBACK QUALITY
            // =============================
            double c1Range = c1.high - c1.low;
            if (c1Range <= 0)
                return false;

            double c1Body = Math.Abs(c1.close - c1.open);
            double c1BodyRatio = c1Body / c1Range;

            bool healthyPullback =
                c1Body <= atr * pullbackBodyLimitAtr &&
                c1BodyRatio <= 0.75;

            if (!healthyPullback)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback blocked: unhealthy pullback | " +
                    $"body={c1Body:F5}, bodyRatio={c1BodyRatio:F2}"
                );
                return false;
            }

            // =============================
            // EMA PROXIMITY / WICK INTERACTION
            // Improved:
            // - Allows close near EMA9
            // - Allows wick touch/rejection around EMA9
            // - Allows controlled pullback toward EMA21
            // =============================
            double tolerance = atr * pullbackToleranceAtr;

            bool closeNearEma9 =
                Math.Abs(c1.close - ema9Prev) <= tolerance;

            bool wickTouchedEma9 =
                bullishTrend
                    ? c1.low <= ema9Prev + tolerance && c1.close >= ema9Prev - tolerance
                    : c1.high >= ema9Prev - tolerance && c1.close <= ema9Prev + tolerance;

            bool shallowPullback =
                Math.Abs(c1.close - ema9Prev) <= atr * 1.20;

            bool pulledTowardEma21 =
                bullishTrend
                    ? c1.low <= ema21Prev + atr * 0.30
                    : c1.high >= ema21Prev - atr * 0.30;

            bool validPullback =
                closeNearEma9 ||
                wickTouchedEma9 ||
                shallowPullback ||
                pulledTowardEma21;

            if (!validPullback)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback blocked: not near EMA pullback zone | " +
                    $"close={c1.close:F5}, ema9={ema9Prev:F5}, ema21={ema21Prev:F5}"
                );
                return false;
            }

            // =============================
            // MOMENTUM
            // =============================
            bool earlyPhase = c1Body < atr * 0.50;

            bool momentumOk =
                earlyPhase
                    ? momentum >= minMomentum * 0.65
                    : momentum >= minMomentum * 0.85;

            if (!momentumOk)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback blocked: momentum too weak | " +
                    $"momentum={momentum:F2}, need>={(earlyPhase ? minMomentum * 0.65 : minMomentum * 0.85):F2}"
                );
                return false;
            }

            // =============================
            // CONTINUATION STRUCTURE
            // =============================
            double range0 = c0.high - c0.low;
            double body0 = Math.Abs(c0.close - c0.open);

            if (range0 <= 0)
                return false;

            double bodyRatio = body0 / range0;

            bool bullishContinuation =
                c0.close > c0.open &&
                bodyRatio >= continuationBodyRatioRequired;

            bool bearishContinuation =
                c0.close < c0.open &&
                bodyRatio >= continuationBodyRatioRequired;

            // =============================
            // MOMENTUM REBUILD
            // Directional now.
            // =============================
            double prevMove = Math.Abs(c1.close - c1.open);
            double moveBuy = Math.Abs(ask - c0.open);
            double moveSell = Math.Abs(bid - c0.open);

            bool momentumRebuildBuy =
                ask > c0.open &&
                moveBuy > Math.Max(prevMove * 0.55, atr * 0.18);

            bool momentumRebuildSell =
                bid < c0.open &&
                moveSell > Math.Max(prevMove * 0.55, atr * 0.18);

            bool acceptableBuyContinuation =
                bullishContinuation || momentumRebuildBuy;

            bool acceptableSellContinuation =
                bearishContinuation || momentumRebuildSell;

            // =============================
            // BREAK ENTRY TRIGGER
            // =============================
            bool breakBuy = ask > c1.high;
            bool breakSell = bid < c1.low;

            // =============================
            // ENTRY
            // =============================
            if (bullishTrend &&
                validPullback &&
                acceptableBuyContinuation &&
                momentumOk &&
                breakBuy)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback BUY | " +
                    $"momentum={momentum:F2}, bodyRatio={bodyRatio:F2}, " +
                    $"distEMA9={distBuyEma9:F5}, breakBuy={breakBuy}, strongTrend={strongTrend}"
                );

                action = TradeAction.EnterBuy;
                return true;
            }

            if (bearishTrend &&
                validPullback &&
                acceptableSellContinuation &&
                momentumOk &&
                breakSell)
            {
                LogFile.Write(
                    $"[{symbol}] Pullback SELL | " +
                    $"momentum={momentum:F2}, bodyRatio={bodyRatio:F2}, " +
                    $"distEMA9={distSellEma9:F5}, breakSell={breakSell}, strongTrend={strongTrend}"
                );

                action = TradeAction.EnterSell;
                return true;
            }

            LogFile.Write(
                $"[{symbol}] Pullback blocked: final conditions failed | " +
                $"bullTrend={bullishTrend}, bearTrend={bearishTrend}, " +
                $"validPullback={validPullback}, buyCont={acceptableBuyContinuation}, sellCont={acceptableSellContinuation}, " +
                $"momentumOk={momentumOk}, breakBuy={breakBuy}, breakSell={breakSell}"
            );

            return false;
        }
    }
}
