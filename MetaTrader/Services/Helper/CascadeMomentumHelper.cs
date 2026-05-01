using MetaTrader.Enums;
using MtApi5;
using System;

namespace MetaTrader.Services.Helper
{
    public static class CascadeMomentumHelper
    {
        // =========================
        // Candle Access
        // =========================
        private static MqlRates GetLastClosed(MqlRates[] rates)
        {
            return rates[^1]; // your definition: last closed
        }

        private static MqlRates GetPrevious(MqlRates[] rates)
        {
            return rates[^2];
        }

        // =========================
        // Profile tuning (STRONGER than velocity)
        // =========================
        private static double GetMomentumFactor(VelocityProfile profile)
        {
            return profile switch
            {
                VelocityProfile.Gold => 0.85,
                VelocityProfile.Index => 0.75,
                VelocityProfile.Crypto => 0.80,
                _ => 0.65 // FX
            };
        }

        private static double GetAtrFactor(VelocityProfile profile)
        {
            return profile switch
            {
                VelocityProfile.Gold => 0.75,
                VelocityProfile.Index => 0.65,
                VelocityProfile.Crypto => 0.70,
                _ => 0.55
            };
        }

        // =========================
        // SELL Cascade Momentum
        // =========================
        public static bool IsStrongCascadeSell(
            MqlRates[] rates,
            double atr,
            double bid,
            VelocityProfile profile)
        {
            if (rates == null || rates.Length < 4 || atr <= 0)
                return false;

            var c0 = GetLastClosed(rates);
            var c1 = GetPrevious(rates);

            double prevMove = Math.Abs(c1.close - c1.open);
            double move = Math.Abs(bid - c0.open);

            double safePrevMove = Math.Max(prevMove, atr * 0.25);

            double momentumFactor = GetMomentumFactor(profile);
            double atrFactor = GetAtrFactor(profile);

            // 🔥 EXTRA: require expansion from previous candle
            double range0 = c0.high - c0.low;
            double range1 = c1.high - c1.low;

            bool expansion = range0 >= range1 * 0.9; // not shrinking

            return
                expansion &&
                move >= safePrevMove * momentumFactor &&
                move >= atr * atrFactor;
        }

        // =========================
        // BUY Cascade Momentum
        // =========================
        public static bool IsStrongCascadeBuy(
            MqlRates[] rates,
            double atr,
            double ask,
            VelocityProfile profile)
        {
            if (rates == null || rates.Length < 4 || atr <= 0)
                return false;

            var c0 = GetLastClosed(rates);
            var c1 = GetPrevious(rates);

            double prevMove = Math.Abs(c1.close - c1.open);
            double move = Math.Abs(ask - c0.open);

            double safePrevMove = Math.Max(prevMove, atr * 0.25);

            double momentumFactor = GetMomentumFactor(profile);
            double atrFactor = GetAtrFactor(profile);

            double range0 = c0.high - c0.low;
            double range1 = c1.high - c1.low;

            bool expansion = range0 >= range1 * 0.9;

            return
                expansion &&
                move >= safePrevMove * momentumFactor &&
                move >= atr * atrFactor;
        }
    }
}
