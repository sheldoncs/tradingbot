namespace MetaTrader.Util
{
    using MtApi5;
    using System;

    public static class SmartCooldownEngine
    {
        private static int _cooldownRemaining = 0;
        private static DateTime _lastCandleTime = DateTime.MinValue;

        // ------------------------------------------------------------
        // PUBLIC: Call this every tick to update cooldown
        // ------------------------------------------------------------
        public static void UpdateCooldown(MqlRates[] rates1H)
        {
            var last = rates1H[^1];

            // Only decrement when a new candle forms
            if (last.time != _lastCandleTime)
            {
                if (_cooldownRemaining > 0)
                {
                    _cooldownRemaining--;
                    Console.WriteLine($"⏳ Cooldown reduced → {_cooldownRemaining}");
                    LogFile.Write($"⏳ Cooldown reduced → {_cooldownRemaining}");
                }

                _lastCandleTime = last.time;
            }
        }

        // ------------------------------------------------------------
        // PUBLIC: Returns true if cooldown should BLOCK normal entries
        // ------------------------------------------------------------
        public static bool IsCooldownBlocking(
            double momentumScore,
            double dynamicThreshold,
            bool bullishBreakout,
            bool bearishBreakout,
            bool strongBullish,
            bool strongBearish
        )
        {
            if (_cooldownRemaining <= 0)
                return false; // no cooldown active

            // Determine if signal is strong enough to bypass cooldown
            bool strongBreakout = bullishBreakout || bearishBreakout || strongBullish || strongBearish;
            bool strongMomentum = momentumScore >= dynamicThreshold * 1.25;

            bool bypass = strongBreakout || strongMomentum;

            if (bypass)
            {
                Console.WriteLine("⚡ Cooldown bypassed (strong signal)");
                LogFile.Write("⚡ Cooldown bypassed (strong signal)");
                return false;
            }

            Console.WriteLine($"⛔ Cooldown BLOCKING entry ({_cooldownRemaining} candles left)");
            LogFile.Write($"⛔ Cooldown BLOCKING entry ({_cooldownRemaining} candles left)");
            return true;
        }

        // ------------------------------------------------------------
        // PUBLIC: Start cooldown after a losing trade
        // ------------------------------------------------------------
        public static void StartCooldown(int candles = 2)
        {
            _cooldownRemaining = candles;

            Console.WriteLine($"🔻 Cooldown started → {_cooldownRemaining} candles");
            LogFile.Write($"🔻 Cooldown started → {_cooldownRemaining} candles");
        }

        // ------------------------------------------------------------
        // OPTIONAL: Expose remaining cooldown (for debugging)
        // ------------------------------------------------------------
        public static int Remaining => _cooldownRemaining;
    }

}
