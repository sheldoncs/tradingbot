using MetaTrader.Data.Entities;
using System;

namespace MetaTrader.Util
{
    public static class TradeAttemptLimiter
    {
        private static DateTime _lastTradeCloseTime = DateTime.MinValue;
        private static TradeSide? _lastTradeSide = null;
        private static string _lastSymbol = null;

        // Configurable cooldown (professional default: 1–2 candles)
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Call this when a trade CLOSES
        /// </summary>
        public static void RegisterTradeClose(string symbol, TradeSide side)
        {
            _lastTradeCloseTime = DateTime.UtcNow;
            _lastTradeSide = side;
            _lastSymbol = symbol;
        }

        /// <summary>
        /// Call this BEFORE allowing a new entry
        /// </summary>
        public static bool IsReentryBlocked(string symbol, TradeSide side)
        {
            if (_lastSymbol != symbol)
                return false;

            if (_lastTradeSide != side)
                return false;

            var elapsed = DateTime.UtcNow - _lastTradeCloseTime;

            if (elapsed < Cooldown)
            {
                Console.WriteLine($"⛔ Re-entry blocked ({elapsed.Minutes}m since last {side})");
                LogFile.Write($"⛔ Re-entry blocked ({elapsed.Minutes}m since last {side})");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Optional reset on clear structure change
        /// </summary>
        public static void Reset()
        {
            _lastTradeSide = null;
            _lastSymbol = null;
            _lastTradeCloseTime = DateTime.MinValue;
        }
    }

}
