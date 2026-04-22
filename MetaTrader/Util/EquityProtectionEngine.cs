using System;

namespace MetaTrader.Util
{
    public static class EquityProtectionEngine
    {
        private static readonly object _lock = new();

        private static bool _initialized;
        private static bool _armed;

        private static double _startEquity;
        private static double _maxEquity;

        private static DateTime _armedAtCandle;

        // --- ATR multipliers ---
        private const double ARM_ATR_MULTIPLIER = 0.6;
        private const double GIVEBACK_ATR_MULTIPLIER = 0.35;

        private const int MIN_CANDLES_AFTER_ARM = 1;

        // --- 🔥 NEW: Absolute minimum USD floors ---
        private const double MIN_ARM_USD = 15.0;        // must earn at least $15 before arming
        private const double MIN_GIVEBACK_USD = 10.0;   // allow at least $10 pullback

        public static double StartEquity { get { lock (_lock) return _startEquity; } }
        public static double MaxEquity { get { lock (_lock) return _maxEquity; } }
        public static bool IsArmed { get { lock (_lock) return _armed; } }

        public static void Initialize(double currentEquity)
        {
            lock (_lock)
            {
                _initialized = true;
                _armed = false;

                _startEquity = currentEquity;
                _maxEquity = currentEquity;
                _armedAtCandle = DateTime.MinValue;
            }
        }

        public static void ResetForNewCycle(double currentEquity)
        {
            Initialize(currentEquity);
        }

        public static bool ShouldCloseAll(
            double currentEquity,
            double atrUsdEquivalent,
            DateTime currentCandleTime)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    Initialize(currentEquity);
                    return false;
                }

                // Track peak
                if (currentEquity > _maxEquity)
                    _maxEquity = currentEquity;

                double profitFromStart = _maxEquity - _startEquity;

                // --- 🔥 Arm threshold with USD floor ---
                double armThreshold =
                    Math.Max(atrUsdEquivalent * ARM_ATR_MULTIPLIER, MIN_ARM_USD);

                if (!_armed && profitFromStart >= armThreshold)
                {
                    _armed = true;
                    _armedAtCandle = currentCandleTime;
                }

                if (!_armed)
                    return false;

                // Avoid same-candle exit
                if ((currentCandleTime - _armedAtCandle).TotalHours < MIN_CANDLES_AFTER_ARM)
                    return false;

                // --- 🔥 Giveback threshold with USD floor ---
                double allowedGiveback =
                    Math.Max(atrUsdEquivalent * GIVEBACK_ATR_MULTIPLIER, MIN_GIVEBACK_USD);

                return currentEquity <= (_maxEquity - allowedGiveback);
            }
        }
    }
}
