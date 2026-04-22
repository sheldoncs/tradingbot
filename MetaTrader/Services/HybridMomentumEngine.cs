using MetaTrader.Util;
using MtApi5;
using System;

namespace MetaTrader.Services
{
    public static class HybridMomentumEngine
    {
        public class MomentumResult
        {
            public double PriceMomentum { get; set; }
            public double H1Change { get; set; }
            public double Hybrid { get; set; }

            public bool Strong { get; set; }
            public bool Breakout { get; set; }
            public bool Impulse { get; set; }

            // 🔥 NEW
            public bool CompressionBreakout { get; set; }
            // 🔥 NEW SLOPE FEATURES
            public bool Rising { get; set; }
            public bool Falling { get; set; }
            public bool Accelerating { get; set; }
            public bool Decelerating { get; set; }
        }



        /// <summary>
        /// Computes real-time candle momentum based on:
        ///  • price movement (range + body)
        ///  • EMA acceleration (h1Change)
        ///  • hybrid combination
        /// </summary>
        public static MomentumResult Calculate(
            MqlRates last,
            MqlRates prev,
            MqlRates[] rates1H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw, bool inloop)
        {
            // -----------------------------
            // PRICE ACTION MOMENTUM
            // -----------------------------
            double candleRange = last.high - last.low;
            double candleBody = Math.Abs(last.close - last.open);

            double priceMomentum = (candleRange + candleBody) / atrRaw;

            // -----------------------------
            // EMA MOMENTUM
            // -----------------------------
            int i1 = emaFast1H.Length - 1;
            int i0 = i1 - 1;

            double h1Momentum = emaFast1H[i1] - emaSlow1H[i1];
            double h1PrevMomentum = emaFast1H[i0] - emaSlow1H[i0];
            double h1Change = h1Momentum - h1PrevMomentum;

            // -----------------------------
            // HYBRID MOMENTUM
            // -----------------------------
            double hybrid = (Math.Abs(h1Change) + priceMomentum) / 2.0;
            // -----------------------------
            // CLASSIFICATION
            // -----------------------------
            bool strong = hybrid >= 0.60;
            bool breakout = hybrid >= 1.00;  // significant move
            bool impulse = hybrid >= 1.50;  // explosive push

            bool compressionBreakout =
              breakout && IsCompressionExpansion(rates1H);

            if (compressionBreakout && !inloop)
            {
                Console.WriteLine("⚡ Compression → Expansion breakout detected");
                LogFile.Write("Compression → Expansion breakout detected");
            }


            // -----------------------------
            // LOGGING OUTPUT
            // -----------------------------
            if (!inloop)
            {
                Console.WriteLine(
                    $"[HybridMomentum] range={candleRange:F2}, body={candleBody:F2}, " +
                    $"pm={priceMomentum:F2}, h1chg={h1Change:F4}, hybrid={hybrid:F2}");

                LogFile.Write(
                    $"HybridMomentum range={candleRange:F2}, body={candleBody:F2}, " +
                    $"pm={priceMomentum:F2}, h1chg={h1Change:F4}, hybrid={hybrid:F2}");
            }

            // -----------------------------
            // MOMENTUM SLOPE DETECTION
            // -----------------------------
            double prevHybrid = 0;

            // Try to estimate previous hybrid using prev candle
            double prevRange = prev.high - prev.low;
            double prevBody = Math.Abs(prev.close - prev.open);
            double prevPriceMomentum = (prevRange + prevBody) / atrRaw;

            // Approximate previous EMA change
            double prevH1Momentum = emaFast1H[i0] - emaSlow1H[i0];
            double prevH1PrevMomentum = emaFast1H[i0 - 1] - emaSlow1H[i0 - 1];
            double prevH1Change = prevH1Momentum - prevH1PrevMomentum;

            double prevNormalizedH1 = Math.Abs(prevH1Change) / atrRaw;

            prevHybrid = (prevNormalizedH1 + prevPriceMomentum) / 2.0;

            // -----------------------------
            // SLOPE FLAGS
            // -----------------------------
            bool rising = hybrid > prevHybrid;
            bool falling = hybrid < prevHybrid;

            bool accelerating = hybrid - prevHybrid > 0.5;
            bool decelerating = prevHybrid - hybrid > 0.5;

            return new MomentumResult
            {
                PriceMomentum = priceMomentum,
                H1Change = h1Change,
                Hybrid = hybrid,
                Strong = strong,
                Breakout = breakout,
                Impulse = impulse,
                CompressionBreakout = compressionBreakout,
                Rising = rising,
                Falling = falling,
                Accelerating = accelerating,
                Decelerating = decelerating
            };
        }

        private static bool IsCompressionExpansion(
            MqlRates[] rates,
            int compressionLookback = 6,
            int recentLookback = 3,
            double expansionMultiplier = 1.35)
        {
            if (rates == null || rates.Length < compressionLookback + recentLookback + 2)
                return false;

            // Use LAST CLOSED candle
            var last = rates[^2];

            double compressionAvg = 0;
            for (int i = rates.Length - compressionLookback - 2; i < rates.Length - 2; i++)
                compressionAvg += rates[i].high - rates[i].low;
            compressionAvg /= compressionLookback;

            double recentAvg = 0;
            for (int i = rates.Length - recentLookback - 2; i < rates.Length - 2; i++)
                recentAvg += rates[i].high - rates[i].low;
            recentAvg /= recentLookback;

            double lastRange = last.high - last.low;

            bool compression = compressionAvg < recentAvg * 0.85;
            bool expansion = lastRange > recentAvg * expansionMultiplier;

            return compression && expansion;
        }

    }

}
