using System;

namespace MetaTrader.Util
{
    public static class ActiveSymbolEngine
    {
        private static string _activeSymbol = null;
        private static double _bestScore = double.MinValue;
        private static DateTime _lastCandleTime = DateTime.MinValue;

        /// <summary>
        /// Called once per symbol per candle to update symbol ranking
        /// </summary>
        public static void Update(string symbol, double score, DateTime candleTime)
        {
            // Reset ranking on new candle
            if (candleTime != _lastCandleTime)
            {
                _bestScore = double.MinValue;
                _activeSymbol = null;
                _lastCandleTime = candleTime;
            }

            if (score > _bestScore)
            {
                _bestScore = score;
                _activeSymbol = symbol;
            }
        }

        /// <summary>
        /// Returns true only for the best symbol of the current candle
        /// </summary>
        public static bool IsActive(string symbol)
        {
            return symbol == _activeSymbol;
        }

        /// <summary>
        /// Optional manual reset (rarely needed)
        /// </summary>
        public static void Reset()
        {
            _activeSymbol = null;
            _bestScore = double.MinValue;
            _lastCandleTime = DateTime.MinValue;
        }
    }
}
