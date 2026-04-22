using System;

namespace MetaTrader.Util
{
    public static class DecemberRiskGuard
    {
        private static double _decemberStartEquity;
        private static bool _initialized = false;

        public static bool TradingDisabled { get; private set; }

        public static void Initialize(double currentEquity)
        {
            if (_initialized) return;

            _decemberStartEquity = currentEquity;
            _initialized = true;

            Log("DecemberRiskGuard initialized");
            Log($"December start equity: {_decemberStartEquity:F2}");
        }

        public static void Evaluate(double currentEquity)
        {
            if (!_initialized || TradingDisabled)
                return;

            double drawdownPercent =
                (_decemberStartEquity - currentEquity) / _decemberStartEquity * 100.0;

            if (drawdownPercent >= 2.0)
            {
                TradingDisabled = true;

                Log("🚨 DECEMBER RISK CAP HIT 🚨");
                Log($"Drawdown: {drawdownPercent:F2}%");
                Log("Trading DISABLED until January.");
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine(message);
            // LogFile.Write(message);
        }
    }

}
