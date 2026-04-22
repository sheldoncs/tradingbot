using MtApi5;
using System;
using System.Collections.Generic;

namespace MetaTrader.Util
{
    public static class VolatilityGuard
    {
        private static readonly Dictionary<string, DateTime> _lastShockTime = new();
        private static readonly Dictionary<string, double> _shockPeakAtr = new();

        private static readonly object _lock = new();

        private const int BlockCandles = 3;
        private const double ShockMultiplier = 2.0;
        private const double MinBodyRatio = 0.6;
        private const double AtrNormalizeFactor = 0.85;

        public static void UpdateShockStatus(
            string symbol,
            MqlRates[] rates,
            double atrRaw)
        {
            if (rates == null || rates.Length < 2)
                return;

            var lastClosed = rates[^2];

            if (IsShockCandle(lastClosed, atrRaw))
            {
                lock (_lock)
                {
                    _lastShockTime[symbol] = lastClosed.time;
                    _shockPeakAtr[symbol] = atrRaw;
                }

                Console.WriteLine($"⚠ Shock recorded for {symbol}");
            }
        }

        public static bool IsShockBlocking(
            string symbol,
            DateTime currentTime,
            double atrRaw)
        {
            lock (_lock)
            {
               
                if (!_lastShockTime.ContainsKey(symbol))
                    return false;

                DateTime shockTime = _lastShockTime[symbol];
                double peakAtr = _shockPeakAtr.GetValueOrDefault(symbol);

                bool stillWithinTimeBlock =
                    currentTime <= shockTime.AddHours(BlockCandles);

                bool atrStillElevated =
                    peakAtr > 0 && atrRaw > peakAtr * AtrNormalizeFactor;

                if (!stillWithinTimeBlock && !atrStillElevated)
                {
                    _lastShockTime.Remove(symbol);
                    _shockPeakAtr.Remove(symbol);
                    return false;
                }

                return true;
            }
        }

        private static bool IsShockCandle(
            MqlRates candle,
            double atr)
        {
            double range = candle.high - candle.low;
            if (range <= 0)
                return false;

            double body = Math.Abs(candle.close - candle.open);

            bool largeRange = range >= atr * ShockMultiplier;
            bool strongBody = (body / range) >= MinBodyRatio;

            return largeRange && strongBody;
        }
    }

}
