using MetaTrader.Util;

namespace MetaTrader.Services.Helper
{
    public static class EquityGivebackProtection
    {
        private static readonly object _lock = new();

        private static bool _initialized;
        private static double _startEquity;
        private static double _peakEquity;

        private class Profile
        {
            public double ActivationProfitUsd { get; set; }
            public double AllowedGivebackUsd { get; set; }
            public double HardDailyLossUsd { get; set; }
        }

        public static void Initialize(double currentEquity)
        {
            lock (_lock)
            {
                _initialized = true;
                _startEquity = currentEquity;
                _peakEquity = currentEquity;
            }
        }

        public static void Reset(double currentEquity)
        {
            Initialize(currentEquity);
        }

        public static bool ShouldCloseAll(
            double currentEquity,
            string accountMode = "FTMO10K")
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    Initialize(currentEquity);
                    return false;
                }

                var profile = GetProfile(accountMode);

                if (currentEquity > _peakEquity)
                    _peakEquity = currentEquity;

                double profitFromStart = _peakEquity - _startEquity;
                double drawdownFromStart = _startEquity - currentEquity;
                double givebackFromPeak = _peakEquity - currentEquity;

                if (drawdownFromStart >= profile.HardDailyLossUsd)
                {
                    LogFile.Write(
                        $"🛑 EQUITY HARD LOSS EXIT | equity={currentEquity:F2}, " +
                        $"start={_startEquity:F2}, loss={drawdownFromStart:F2}, " +
                        $"maxLoss={profile.HardDailyLossUsd:F2}"
                    );

                    return true;
                }

                if (profitFromStart < profile.ActivationProfitUsd)
                {
                    LogFile.Write(
                        $"EquityGiveback hold: not activated | equity={currentEquity:F2}, " +
                        $"peak={_peakEquity:F2}, profitFromStart={profitFromStart:F2}, " +
                        $"need={profile.ActivationProfitUsd:F2}"
                    );

                    return false;
                }

                if (givebackFromPeak >= profile.AllowedGivebackUsd)
                {
                    LogFile.Write(
                        $"🛑 EQUITY GIVEBACK EXIT | equity={currentEquity:F2}, " +
                        $"peak={_peakEquity:F2}, giveback={givebackFromPeak:F2}, " +
                        $"allowed={profile.AllowedGivebackUsd:F2}"
                    );

                    return true;
                }

                return false;
            }
        }

        private static Profile GetProfile(string accountMode)
        {
            string mode = accountMode?.ToUpperInvariant() ?? "";

            if (mode.Contains("FTMO10K"))
            {
                return new Profile
                {
                    ActivationProfitUsd = 15.00,
                    AllowedGivebackUsd = 8.00,
                    HardDailyLossUsd = 75.00
                };
            }

            return new Profile
            {
                ActivationProfitUsd = 15.00,
                AllowedGivebackUsd = 8.00,
                HardDailyLossUsd = 75.00
            };
        }
    }
}
