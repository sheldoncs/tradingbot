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

        public static double MinAtrPercentile = 0.55; // lowered
        public static double MoveFromOpenFactor = 0.5; // slightly easier
        public static double MaxSlippageFactor = 0.30;
        public static int CooldownMinutes = 45;
        public static int VelocityWindowMinutes = 25;

        public static double ShockMultiplier = 2.2; // less aggressive blocking
        public static double ShockBodyRatio = 0.65;

        public static double MinEmaSeparationAtr = 0.10;
        public static double PreviousImpulseBlock = 1.6; // relaxed

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

            if (ctx.State != MarketState.Armed)
                return false;

            if (atrPercentile < MinAtrPercentile)
                return false;

            double bid = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID);
            double ask = client.SymbolInfoDouble(symbol, ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK);

            // ===============================
            // SPREAD
            // ===============================
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
                if ((DateTime.UtcNow - lastTime).TotalMinutes < CooldownMinutes)
                    return false;
            }

            // ===============================
            // RATES
            // ===============================
            client.CopyRates(symbol, ENUM_TIMEFRAMES.PERIOD_H1, 1, 4, out MqlRates[] rates);
            if (rates == null || rates.Length < 4)
                return false;

            var c0 = rates[^1];
            var c1 = rates[^2];

            // ===============================
            // RELAXED IMPULSE BLOCK
            // ===============================
            double prevRange = c1.high - c1.low;

            if (prevRange > atr * PreviousImpulseBlock)
                return false;

            // ===============================
            // LIVE MOVE
            // ===============================
            double liveOpen = c0.open;

            double moveBuy = Math.Abs(ask - liveOpen);
            double moveSell = Math.Abs(bid - liveOpen);

            // ===============================
            // EMA
            // ===============================
            double ema9 = emaFast[^1];
            double ema21 = emaSlow[^1];

            double fastSlope = emaFast[^1] - emaFast[^2];

            // allow more extension
            double maxDistance =
                profile == VelocityProfile.Gold ? 1.4 :
                profile == VelocityProfile.FX ? 1.0 :
                profile == VelocityProfile.Index ? 1.3 :
                1.6;

            // ===============================
            // MOMENTUM (RELAXED)
            // ===============================
            double prevMove = Math.Abs(c1.close - c1.open);

            bool momentumBuy = moveBuy >= prevMove * 0.6;
            bool momentumSell = moveSell >= prevMove * 0.6;

            // ===============================
            // ENTRY
            // ===============================
            bool buy =
                emaFast[^1] > ema21 &&
                fastSlope > 0 &&
                ask > liveOpen &&
                moveBuy > atr * MoveFromOpenFactor &&
                momentumBuy &&
                Math.Abs(ask - ema9) < atr * maxDistance;

            bool sell =
                emaFast[^1] < ema21 &&
                fastSlope < 0 &&
                bid < liveOpen &&
                moveSell > atr * MoveFromOpenFactor &&
                momentumSell &&
                Math.Abs(bid - ema9) < atr * maxDistance;

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

            _lastVelocityEntry[symbol] = DateTime.UtcNow;
            return true;
        }
    }
}
