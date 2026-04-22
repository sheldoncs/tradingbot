using System;
using System.Collections.Generic;

namespace MetaTrader.Util
{
    public static class PostExitCooldown
    {
        private static readonly Dictionary<string, DateTime> _lastForcedExit = new();
        private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

        public static void RegisterForcedExit(string symbol)
        {
            _lastForcedExit[symbol] = DateTime.UtcNow;
        }

        public static bool IsInCooldown(string symbol)
        {
            if (!_lastForcedExit.TryGetValue(symbol, out var lastExit))
                return false;

            return (DateTime.UtcNow - lastExit) < Cooldown;
        }
    }

}
