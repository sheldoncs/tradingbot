using MetaTrader.Enums;
using MetaTrader.Util;
using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public static class VelocityEntryEngine
    {
        private static readonly Dictionary<string, DateTime> _lastVelocityEntry = new();

        public static double MinAtrPercentile = 0.70;
        public static double MoveFromOpenFactor = 0.6;
        public static double MaxSlippageFactor = 0.25;
        public static int CooldownMinutes = 60;
        public static int VelocityWindowMinutes = 15;

        public static double ShockMultiplier = 1.8;
        public static double ShockBodyRatio = 0.6;
        public static int ShockLookbackCandles = 2;

        public static double MinEmaSeparationAtr = 0.15;
        public static double PreviousImpulseBlock = 1.25;

        private static int vCounter = 0;

        public static void velocityCounter()
        {
            LogFile.Write($"Velocity entries count: {vCounter + 1}");
            vCounter++;
        }

        public static bool ShouldEnter(
            string symbol,
            MtApi5Client client,
            double atr,
            double atrPercentile,
            double[] emaFast,
            double[] emaSlow,
            SymbolContext ctx,
            VelocityProfile profile,
            out TradeAction action)
        {
            action = TradeAction.Hold;

            if (EmaCompressionFilter.IsCompressed(emaFast, emaSlow, atr, symbol))
            {
                LogFile.Write($"Blocked entry due to EMA compression: {symbol}");
                return false;
            }

            if (ctx.State != MarketState.Armed)
                return false;

            // ===============================
            // PROFILE SETTINGS
            // ===============================
            double minAtr = MinAtrPercentile;
            double moveFactor = MoveFromOpenFactor;
            double shockMult = ShockMultiplier;
            int cooldown = CooldownMinutes;
            double maxDistanceAtr = 1.0;

            switch (profile)
            {
                case VelocityProfile.Index:
                    minAtr = 0.60;
                    moveFactor = 0.50;
                    shockMult = 2.2;
                    cooldown = 45;
                    maxDistanceAtr = 1.0;
                    break;

                case VelocityProfile.Gold:
                    minAtr = 0.70;
                    moveFactor = 0.60;
                    shockMult = 1.8;
                    cooldown = 60;
                    maxDistanceAtr = 0.8;
                    break;

                case VelocityProfile.FX:
                    minAtr = 0.78;
                    moveFactor = 0.75;
                    shockMult = 1.6;
                    cooldown = 90;
                    maxDistanceAtr = 0.6;
                    break;

                default: // CRYPTO
                    minAtr = 0.65;
                    moveFactor = 0.65;
                    shockMult = 2.0;
                    cooldown = 60;
                    maxDistanceAtr = 1.2;
                    break;
            }

            if (atrPercentile < minAtr)
                return false;

            // ===============================
            // SPREAD
            // ===============================
            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            long spreadPoints = client.SymbolInfoInteger(symbol, ENUM_SYMBOL_INFO_INTEGER.SYMBOL_SPREAD);
            double point = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_POINT);
            double spread = spreadPoints * point;

            if (spread > atr * MaxSlippageFactor)
                return false;

            // ===============================
            // COOLDOWN
            // ===============================
            if (_lastVelocityEntry.TryGetValue(symbol, out DateTime lastTime))
            {
                if ((DateTime.UtcNow - lastTime).TotalMinutes < cooldown)
                    return false;
            }

            // ===============================
            // CLOSED CANDLES (STRUCTURE)
            // ===============================
            client.CopyRates(symbol, ENUM_TIMEFRAMES.PERIOD_H1, 1, ShockLookbackCandles + 2, out MqlRates[] rates);

            if (rates == null || rates.Length < ShockLookbackCandles + 2)
                return false;

            var current = rates[^1];
            var prev = rates[^2];

            double prevRange = prev.high - prev.low;

            double prevImpulseLimit = atr * PreviousImpulseBlock;
            if (profile == VelocityProfile.Index)
                prevImpulseLimit = atr * 2.0;

            if (prevRange > prevImpulseLimit)
            {
                LogFile.Write($"Velocity blocked — previous candle already impulse: {symbol}");
                return false;
            }

            // ===============================
            // LIVE CANDLE (KEY FIX)
            // ===============================
            double liveOpen = current.open;
            double liveHigh = Math.Max(current.high, Math.Max(bid, ask));
            double liveLow = Math.Min(current.low, Math.Min(bid, ask));

            double liveRange = liveHigh - liveLow;
            double liveBodyBuy = Math.Abs(ask - liveOpen);
            double liveBodySell = Math.Abs(bid - liveOpen);

            double moveBuy = Math.Abs(ask - liveOpen);
            double moveSell = Math.Abs(bid - liveOpen);

            // ===============================
            // SHOCK
            // ===============================
            bool shock =
                liveRange >= atr * shockMult &&
                (Math.Max(liveBodyBuy, liveBodySell) / Math.Max(liveRange, 0.00001)) >= ShockBodyRatio;

            if (shock || ctx.IsShockActive)
                return false;

            if (!ExpansionFreshnessFilter.IsFresh(rates, atr, profile))
                return false;

            double minutesIntoCandle = (DateTime.UtcNow - current.time).TotalMinutes;
            if (minutesIntoCandle > VelocityWindowMinutes)
                return false;

            // ===============================
            // EMA
            // ===============================
            double ema9 = emaFast[^1];
            double emaSlowVal = emaSlow[^1];

            double fastSlope = emaFast[^1] - emaFast[^2];
            double slowSlope = emaSlow[^1] - emaSlow[^2];

            double emaDistance = Math.Abs(emaFast[^1] - emaSlow[^1]);
            if (emaDistance < atr * MinEmaSeparationAtr)
                return false;

            // ===============================
            // MOMENTUM BUILDING
            // ===============================
            double prevMove = Math.Abs(prev.close - prev.open);

            bool momentumBuy = moveBuy > prevMove;
            bool momentumSell = moveSell > prevMove;

            // ===============================
            // BUY CONDITIONS
            // ===============================
            bool buy =
                emaFast[^1] > emaSlowVal &&
                fastSlope > 0 &&
                ask > liveOpen &&
                moveBuy > atr * moveFactor &&
                momentumBuy &&
                Math.Abs(ask - ema9) < atr * maxDistanceAtr;

            // ===============================
            // SELL CONDITIONS
            // ===============================
            bool sell =
                emaFast[^1] < emaSlowVal &&
                fastSlope < 0 &&
                bid < liveOpen &&
                moveSell > atr * moveFactor &&
                momentumSell &&
                Math.Abs(bid - ema9) < atr * maxDistanceAtr;

            // ===============================
            // FINAL DECISION
            // ===============================
            if (buy)
            {
                action = TradeAction.EnterBuy;
            }
            else if (sell)
            {
                action = TradeAction.EnterSell;
            }
            else
            {
                return false;
            }

            velocityCounter();
            _lastVelocityEntry[symbol] = DateTime.UtcNow;

            return true;
        }
    }
}
