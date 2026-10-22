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

            public double PreviousHybrid { get; set; }
            public bool Strong { get; set; }
            public bool Breakout { get; set; }
            public bool Impulse { get; set; }

            public bool CompressionBreakout { get; set; }

            public bool Rising { get; set; }
            public bool Falling { get; set; }
            public bool Accelerating { get; set; }
            public bool Decelerating { get; set; }
        }

        // ============================================================
        // EXISTING SIGNATURE
        //
        // Kept so existing callers do not break.
        //
        // NOTE:
        // If last is a closed candle, this remains closed-candle
        // momentum. For live trading decisions, use the overload below.
        // ============================================================
        public static MomentumResult Calculate(
            MqlRates last,
            MqlRates prev,
            MqlRates[] rates1H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw,
            bool inloop)
        {
            return CalculateInternal(
                last,
                prev,
                rates1H,
                emaFast1H,
                emaSlow1H,
                atrRaw,
                inloop,
                false
            );
        }

        // ============================================================
        // LIVE VERSION
        //
        // liveOpen  = opening price of current H1 candle
        // livePrice = current midpoint/bid/ask as appropriate
        //
        // The historical arrays remain closed-candle arrays.
        // ============================================================
        public static MomentumResult CalculateLive(
            double liveOpen,
            double livePrice,
            MqlRates prevClosed,
            MqlRates[] rates1H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw,
            bool inloop)
        {
            // Backward-compatible overload.
            //
            // Existing callers that already calculate a live midpoint can
            // continue using this signature unchanged.
            return CalculateLiveInternal(
                liveOpen,
                livePrice,
                livePrice,
                livePrice,
                prevClosed,
                rates1H,
                emaFast1H,
                emaSlow1H,
                atrRaw,
                inloop
            );
        }


        // ============================================================
        // LIVE BID / ASK VERSION
        //
        // Uses the CURRENT bid and ask on the CURRENT H1 candle.
        //
        // The midpoint is used for:
        //      - live candle close
        //      - live EMA9
        //      - live EMA21
        //
        // Bid / ask are also included when constructing the synthetic
        // current candle high / low so the calculation is based on the
        // current market, while avoiding using either side of the spread
        // as the directional close.
        //
        // Existing callers are NOT broken by this overload.
        // ============================================================
        public static MomentumResult CalculateLive(
            double liveOpen,
            double bid,
            double ask,
            MqlRates prevClosed,
            MqlRates[] rates1H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw,
            bool inloop)
        {
            if (bid <= 0 ||
                ask <= 0 ||
                ask < bid)
            {
                return new MomentumResult();
            }

            double liveMid =
                (bid + ask) / 2.0;

            return CalculateLiveInternal(
                liveOpen,
                liveMid,
                bid,
                ask,
                prevClosed,
                rates1H,
                emaFast1H,
                emaSlow1H,
                atrRaw,
                inloop
            );
        }


        // ============================================================
        // LIVE CALCULATION CORE
        // ============================================================
        private static MomentumResult CalculateLiveInternal(
            double liveOpen,
            double livePrice,
            double bid,
            double ask,
            MqlRates prevClosed,
            MqlRates[] rates1H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw,
            bool inloop)
        {
            if (liveOpen <= 0 ||
                livePrice <= 0 ||
                rates1H == null ||
                rates1H.Length < 3 ||
                emaFast1H == null ||
                emaSlow1H == null ||
                emaFast1H.Length < 3 ||
                emaSlow1H.Length < 3 ||
                atrRaw <= 0)
            {
                return new MomentumResult();
            }

            // --------------------------------------------------------
            // Build a synthetic CURRENT H1 candle.
            //
            // Direction is based on the midpoint, not bid or ask alone.
            // This prevents spread from artificially changing the
            // bullish/bearish direction of HybridMomentum.
            //
            // Current bid/ask are included in high/low so the synthetic
            // candle reflects the current executable market.
            // --------------------------------------------------------
            double liveHigh =
                Math.Max(
                    liveOpen,
                    Math.Max(
                        livePrice,
                        Math.Max(bid, ask)
                    )
                );

            double liveLow =
                Math.Min(
                    liveOpen,
                    Math.Min(
                        livePrice,
                        Math.Min(bid, ask)
                    )
                );

            MqlRates live = new MqlRates
            {
                open = liveOpen,
                close = livePrice,
                high = liveHigh,
                low = liveLow,
                mt_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            // --------------------------------------------------------
            // Calculate LIVE EMA estimates from the most recent
            // CLOSED EMA values using CURRENT midpoint price.
            // --------------------------------------------------------
            double closedEma9 =
                emaFast1H[^1];

            double closedEma21 =
                emaSlow1H[^1];

            double liveEma9 =
                closedEma9 +
                (2.0 / (9.0 + 1.0)) *
                (livePrice - closedEma9);

            double liveEma21 =
                closedEma21 +
                (2.0 / (21.0 + 1.0)) *
                (livePrice - closedEma21);

            // --------------------------------------------------------
            // Append LIVE EMA values instead of replacing the latest
            // CLOSED EMA.
            //
            // Result:
            // [... previous closed EMA, latest closed EMA, LIVE EMA]
            //
            // This is important because CalculateInternal then compares
            // the live EMA spread against the latest CLOSED EMA spread.
            // --------------------------------------------------------
            double[] liveFast =
                new double[emaFast1H.Length + 1];

            double[] liveSlow =
                new double[emaSlow1H.Length + 1];

            Array.Copy(
                emaFast1H,
                liveFast,
                emaFast1H.Length);

            Array.Copy(
                emaSlow1H,
                liveSlow,
                emaSlow1H.Length);

            liveFast[^1] =
                liveEma9;

            liveSlow[^1] =
                liveEma21;

            return CalculateInternal(
                live,
                prevClosed,
                rates1H,
                liveFast,
                liveSlow,
                atrRaw,
                inloop,
                true
            );
        }


        // ============================================================
        // CORE CALCULATION
        // ============================================================
        private static MomentumResult CalculateInternal(
            MqlRates last,
            MqlRates prev,
            MqlRates[] rates1H,
            double[] emaFast1H,
            double[] emaSlow1H,
            double atrRaw,
            bool inloop,
            bool isLive)
        {
            if (rates1H == null ||
                emaFast1H == null ||
                emaSlow1H == null ||
                emaFast1H.Length < 3 ||
                emaSlow1H.Length < 3 ||
                atrRaw <= 0)
            {
                return new MomentumResult();
            }

            // --------------------------------------------------------
            // PRICE ACTION MOMENTUM
            // --------------------------------------------------------
            double candleRange =
                Math.Max(0, last.high - last.low);

            double candleBody =
                Math.Abs(last.close - last.open);

            // LIVE synthetic candle does not have the true intrabar H1
            // high/low history. Its range is derived from liveOpen/current
            // bid/ask, so range and body largely describe the same move.
            // Do not count that movement twice.
            //
            // CLOSED candles have genuine high/low data, so preserve the
            // original (range + body) / ATR calculation for them.
            double priceMomentum =
                isLive
                    ? candleBody / atrRaw
                    : (candleRange + candleBody) / atrRaw;

            // --------------------------------------------------------
            // EMA MOMENTUM
            // --------------------------------------------------------
            int i1 = emaFast1H.Length - 1;
            int i0 = i1 - 1;
            int iPrev = i0 - 1;

            if (iPrev < 0)
                return new MomentumResult();

            double h1Momentum =
                emaFast1H[i1] -
                emaSlow1H[i1];

            double h1PrevMomentum =
                emaFast1H[i0] -
                emaSlow1H[i0];

            double h1Change =
                h1Momentum -
                h1PrevMomentum;

            // --------------------------------------------------------
            // IMPORTANT FIX:
            //
            // Normalize CURRENT EMA momentum by ATR.
            //
            // Previously current hybrid used raw h1Change while
            // previous hybrid used normalized h1Change.
            // --------------------------------------------------------
            double normalizedH1Change =
                Math.Abs(h1Change) / atrRaw;

            // --------------------------------------------------------
            // HYBRID MOMENTUM
            // --------------------------------------------------------
            double hybrid =
                (normalizedH1Change + priceMomentum) / 2.0;

            // --------------------------------------------------------
            // CLASSIFICATION
            // --------------------------------------------------------
            bool strong =
                hybrid >= 0.60;

            bool breakout =
                hybrid >= 1.00;

            bool impulse =
                hybrid >= 1.50;

            bool compressionBreakout =
                breakout &&
                IsCompressionExpansion(rates1H);

            if (compressionBreakout && !inloop)
            {
                Console.WriteLine(
                    "⚡ Compression → Expansion breakout detected");

                LogFile.Write(
                    "Compression → Expansion breakout detected");
            }

            // --------------------------------------------------------
            // PREVIOUS MOMENTUM
            // --------------------------------------------------------
            double prevRange =
                Math.Max(0, prev.high - prev.low);

            double prevBody =
                Math.Abs(prev.close - prev.open);

            double prevPriceMomentum =
                (prevRange + prevBody) / atrRaw;

            double prevH1Momentum =
                emaFast1H[i0] -
                emaSlow1H[i0];

            double prevH1PrevMomentum =
                emaFast1H[iPrev] -
                emaSlow1H[iPrev];

            double prevH1Change =
                prevH1Momentum -
                prevH1PrevMomentum;

            double prevNormalizedH1 =
                Math.Abs(prevH1Change) / atrRaw;

            double prevHybrid =
                (prevNormalizedH1 +
                 prevPriceMomentum) / 2.0;

            // --------------------------------------------------------
            // SLOPE FLAGS
            // --------------------------------------------------------
            bool rising =
                hybrid > prevHybrid;

            bool falling =
                hybrid < prevHybrid;

            bool accelerating =
                hybrid - prevHybrid > 0.50;

            bool decelerating =
                prevHybrid - hybrid > 0.50;

            // --------------------------------------------------------
            // LOGGING
            // --------------------------------------------------------
            if (!inloop)
            {
                Console.WriteLine(
                    $"[HybridMomentum:{(isLive ? "LIVE" : "CLOSED")}] " +
                    $"range={candleRange:F2}, " +
                    $"body={candleBody:F2}, " +
                    $"pm={priceMomentum:F2}, " +
                    $"h1chg={h1Change:F4}, " +
                    $"h1chgAtr={normalizedH1Change:F2}, " +
                    $"hybrid={hybrid:F2}, " +
                    $"prevHybrid={prevHybrid:F2}, " +
                    $"rising={rising}, " +
                    $"falling={falling}"
                );

                LogFile.Write(
                    $"HybridMomentum[{(isLive ? "LIVE" : "CLOSED")}] " +
                    $"range={candleRange:F2}, " +
                    $"body={candleBody:F2}, " +
                    $"pm={priceMomentum:F2}, " +
                    $"h1chg={h1Change:F4}, " +
                    $"h1chgAtr={normalizedH1Change:F2}, " +
                    $"hybrid={hybrid:F2}, " +
                    $"prevHybrid={prevHybrid:F2}, " +
                    $"rising={rising}, " +
                    $"falling={falling}"
                );
            }

            return new MomentumResult
            {
                PriceMomentum = priceMomentum,
                H1Change = h1Change,
                PreviousHybrid = prevHybrid,
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

        // ============================================================
        // COMPRESSION / EXPANSION
        //
        // Deliberately remains based on CLOSED candles.
        // This is structural context, not live trigger direction.
        // ============================================================
        private static bool IsCompressionExpansion(
            MqlRates[] rates,
            int compressionLookback = 6,
            int recentLookback = 3,
            double expansionMultiplier = 1.35)
        {
            if (rates == null ||
                rates.Length <
                compressionLookback +
                recentLookback +
                2)
            {
                return false;
            }

            var lastClosed =
                rates[^2];

            double compressionAvg = 0;

            for (int i =
                     rates.Length -
                     compressionLookback -
                     2;
                 i < rates.Length - 2;
                 i++)
            {
                compressionAvg +=
                    rates[i].high -
                    rates[i].low;
            }

            compressionAvg /=
                compressionLookback;

            double recentAvg = 0;

            for (int i =
                     rates.Length -
                     recentLookback -
                     2;
                 i < rates.Length - 2;
                 i++)
            {
                recentAvg +=
                    rates[i].high -
                    rates[i].low;
            }

            recentAvg /=
                recentLookback;

            if (recentAvg <= 0)
                return false;

            double lastRange =
                lastClosed.high -
                lastClosed.low;

            bool compression =
                compressionAvg <
                recentAvg * 0.85;

            bool expansion =
                lastRange >
                recentAvg *
                expansionMultiplier;

            return
                compression &&
                expansion;
        }
    }

}
