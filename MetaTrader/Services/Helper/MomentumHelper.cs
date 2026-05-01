using MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class MomentumHelper
    {
        // =========================
        // Candle Access (centralized)
        // =========================
        private static MqlRates GetLastClosed(MqlRates[] rates)
        {
            return rates[^1]; // your system: last closed
        }

        private static MqlRates GetPrevious(MqlRates[] rates)
        {
            return rates[^2];
        }

        // =========================
        // Profile tuning
        // =========================
        private static double GetMomentumFactor(VelocityProfile profile)
        {
            return profile switch
            {
                VelocityProfile.Gold => 0.7,
                VelocityProfile.Index => 0.6,
                VelocityProfile.Crypto => 0.65,
                _ => 0.5 // FX
            };
        }

        private static double GetAtrFactor(VelocityProfile profile)
        {
            return profile switch
            {
                VelocityProfile.Gold => 0.6,
                VelocityProfile.Index => 0.5,
                VelocityProfile.Crypto => 0.55,
                _ => 0.4
            };
        }

        // =========================
        // BUY momentum
        // =========================
        public static bool IsStrongVelocityBuy(
            MqlRates[] rates,
            double atr,
            double ask,
            VelocityProfile profile)
        {
            if (rates == null || rates.Length < 3 || atr <= 0)
                return false;

            var lastClosed = GetLastClosed(rates);
            var prev = GetPrevious(rates);

            double prevMove = Math.Abs(prev.close - prev.open);
            double move = Math.Abs(ask - lastClosed.open);

            double safePrevMove = Math.Max(prevMove, atr * 0.2);

            double momentumFactor = GetMomentumFactor(profile);
            double atrFactor = GetAtrFactor(profile);

            return
                move >= safePrevMove * momentumFactor &&
                move >= atr * atrFactor;
        }

        // =========================
        // SELL momentum
        // =========================
        public static bool IsStrongVelocitySell(
            MqlRates[] rates,
            double atr,
            double bid,
            VelocityProfile profile)
        {
            if (rates == null || rates.Length < 3 || atr <= 0)
                return false;

            var lastClosed = GetLastClosed(rates);
            var prev = GetPrevious(rates);

            double prevMove = Math.Abs(prev.close - prev.open);
            double move = Math.Abs(bid - lastClosed.open);

            double safePrevMove = Math.Max(prevMove, atr * 0.2);

            double momentumFactor = GetMomentumFactor(profile);
            double atrFactor = GetAtrFactor(profile);

            return
                move >= safePrevMove * momentumFactor &&
                move >= atr * atrFactor;
        }

        // =========================
        // Optional: unified call
        // =========================
        public static bool IsStrongVelocity(
            MqlRates[] rates,
            double atr,
            double price,
            VelocityProfile profile)
        {
            if (rates == null || rates.Length < 3 || atr <= 0)
                return false;

            var lastClosed = GetLastClosed(rates);
            var prev = GetPrevious(rates);

            double prevMove = Math.Abs(prev.close - prev.open);
            double move = Math.Abs(price - lastClosed.open);

            double safePrevMove = Math.Max(prevMove, atr * 0.2);

            double momentumFactor = GetMomentumFactor(profile);
            double atrFactor = GetAtrFactor(profile);

            return
                move >= safePrevMove * momentumFactor &&
                move >= atr * atrFactor;
        }
    }
}
