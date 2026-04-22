using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Util
{
    public static class SymbolRotationSelector
    {
        private static string _activeSymbol = null;
        private static DateTime _lastSelectionTime = DateTime.MinValue;

        // Only re-evaluate once per H1 candle
        public static string SelectBestSymbol(
            Dictionary<string, double> symbolScores)
        {
            if (DateTime.UtcNow - _lastSelectionTime < TimeSpan.FromHours(1))
                return _activeSymbol;

            _lastSelectionTime = DateTime.UtcNow;

            _activeSymbol = symbolScores
                .OrderByDescending(x => x.Value)
                .Where(x => x.Value >= 2.5) // minimum quality threshold
                .Select(x => x.Key)
                .FirstOrDefault();

            return _activeSymbol;
        }

        public static bool IsActiveSymbol(string symbol)
        {
            return symbol == _activeSymbol;
        }
    }

}
