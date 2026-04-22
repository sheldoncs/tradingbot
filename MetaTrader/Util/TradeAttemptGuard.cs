using System;

namespace MetaTrader.Util
{
    public static class TradeAttemptGuard
    {
        private static DateTime _lastFailedImpulseTime = DateTime.MinValue;
        private static bool _blocked = false;

        public static void MarkFailedImpulse(DateTime impulseTime)
        {
            _lastFailedImpulseTime = impulseTime;
            _blocked = true;

            Console.WriteLine("🚫 Impulse failed — re-entry blocked until reset");
        }

        public static bool CanEnter(bool compressionDetected)
        {
            // Allow new trades only after a reset
            if (_blocked && !compressionDetected)
                return false;

            if (compressionDetected)
                _blocked = false;

            return true;
        }
    }

}
