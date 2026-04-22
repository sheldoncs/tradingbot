using System;

namespace MetaTrader.Util
{
    public static class DynamicLotSizingEngine
    {
        private const double MinLot = 0.01;
        private const double MaxMultiplier = 1.30;

        private const double StrongMomentum = 1.20;
        private const double ExtremeMomentum = 1.50;

        public static double Adjust(
            double baseLot,
            double momentumScore,
            bool impulse,
            bool breakAndHoldConfirmed,
            double maxAllowedLot,
            Action<string>? log = null)
        {
            baseLot = Math.Max(baseLot, MinLot);

            double multiplier = 1.0;

            if (momentumScore >= StrongMomentum)
            {
                multiplier = 1.10;
                log?.Invoke("📈 Lot +10% (strong momentum)");
            }

            if (impulse && breakAndHoldConfirmed)
            {
                multiplier = 1.20;
                log?.Invoke("🚀 Lot +20% (impulse + break & hold)");
            }

            if (momentumScore >= ExtremeMomentum &&
                impulse &&
                breakAndHoldConfirmed)
            {
                multiplier = 1.30;
                log?.Invoke("🔥 Lot +30% (EXTREME momentum)");
            }

            multiplier = Math.Min(multiplier, MaxMultiplier);

            double scaledLot = baseLot * multiplier;
            double finalLot = Math.Min(scaledLot, maxAllowedLot);

            finalLot = Math.Max(finalLot, MinLot);

            log?.Invoke($"🎯 Final lot size: {finalLot:F2}");
            return finalLot;
        }
    }

}

