using System;

namespace MetaTrader.Services.Helper
{
    public static class TransitionZoneEngine
    {
        public enum TransitionState
        {
            Normal,
            Transition,
            HighNoise
        }

        public class TransitionResult
        {
            public TransitionState State { get; set; }

            public bool ReduceLotSize { get; set; }

            public bool WidenStop { get; set; }

            public bool DelayExitConfirmation { get; set; }

            public double StopMultiplier { get; set; }

            public string Reason { get; set; }
        }

        public static TransitionResult Evaluate(
            string symbol,
            double atr,
            double atrPercentile,
            double ema9,
            double ema21,
            double prevEma9,
            double prevEma21,
            double momentum,
            double bodyRatio,
            double rangeAtr,
            double close,
            double prevClose,
            bool trendUp,
            bool trendDown)
        {
            double emaSpread = Math.Abs(ema9 - ema21);

            double emaSlope9 = ema9 - prevEma9;
            double emaSlope21 = ema21 - prevEma21;

            bool emaCompression =
                emaSpread < atr * 0.35;

            bool slopeWeakening =
                Math.Abs(emaSlope9) < atr * 0.10;

            bool conflictingMomentum =
                momentum > 1.0 &&
                momentum < 2.0;

            bool weakBody =
                bodyRatio < 0.40;

            bool noisyExpansion =
                rangeAtr > 1.2 &&
                weakBody;

            bool priceNearEMA =
                Math.Abs(close - ema9) < atr * 0.40;

            bool transitionDetected =
                emaCompression &&
                slopeWeakening &&
                conflictingMomentum &&
                priceNearEMA;

            bool highNoise =
                transitionDetected &&
                noisyExpansion &&
                atrPercentile >= 0.75;

            // -----------------------------
            // HIGH NOISE
            // -----------------------------
            if (highNoise)
            {
                return new TransitionResult
                {
                    State = TransitionState.HighNoise,
                    ReduceLotSize = true,
                    WidenStop = true,
                    DelayExitConfirmation = true,
                    StopMultiplier = GetStopMultiplier(symbol, 1.8),
                    Reason =
                        "High-noise transition zone detected"
                };
            }

            // -----------------------------
            // TRANSITION
            // -----------------------------
            if (transitionDetected)
            {
                return new TransitionResult
                {
                    State = TransitionState.Transition,
                    ReduceLotSize = true,
                    WidenStop = true,
                    DelayExitConfirmation = true,
                    StopMultiplier = GetStopMultiplier(symbol, 1.35),
                    Reason =
                        "Transition zone detected"
                };
            }

            // -----------------------------
            // NORMAL
            // -----------------------------
            return new TransitionResult
            {
                State = TransitionState.Normal,
                ReduceLotSize = false,
                WidenStop = false,
                DelayExitConfirmation = false,
                StopMultiplier = 1.0,
                Reason = "Normal market conditions"
            };
        }

        // --------------------------------
        // SYMBOL-ADAPTIVE STOP MULTIPLIER
        // --------------------------------
        private static double GetStopMultiplier(
            string symbol,
            double baseMultiplier)
        {
            symbol = symbol.ToUpper();

            // Crypto
            if (symbol.Contains("ETH") ||
                symbol.Contains("BTC") ||
                symbol.Contains("BNB") ||
                symbol.Contains("SOL") ||
                symbol.Contains("XRP"))
            {
                return baseMultiplier * 1.4;
            }

            // Gold
            if (symbol.Contains("XAU"))
            {
                return baseMultiplier * 1.25;
            }

            // Indices
            if (symbol.Contains("US100") ||
                symbol.Contains("NAS"))
            {
                return baseMultiplier * 1.2;
            }

            // FX
            return baseMultiplier;
        }
    }
}
