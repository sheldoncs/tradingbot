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

        private class Profile
        {
            public double ArmAtrMultiplier;
            public double GivebackAtrMultiplier;
            public double MinArmUsd;
            public double MinGivebackUsd;
            public double StrongTrendBreathingFactor;
            public int MinCandlesAfterArm;
        }

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
            string symbol,
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

                if (currentEquity > _maxEquity)
                    _maxEquity = currentEquity;

                var profile = GetProfile(symbol);

                double profitFromStart = _maxEquity - _startEquity;

                double armThreshold =
                    Math.Max(
                        atrUsdEquivalent * profile.ArmAtrMultiplier,
                        profile.MinArmUsd
                    );

                if (!_armed && profitFromStart >= armThreshold)
                {
                    _armed = true;
                    _armedAtCandle = currentCandleTime;

                    LogFile.Write(
                        $"[{symbol}] EquityProtection ARMED | " +
                        $"start={_startEquity:F2}, max={_maxEquity:F2}, " +
                        $"profitFromStart={profitFromStart:F2}, armThreshold={armThreshold:F2}, " +
                        $"atrUsdEq={atrUsdEquivalent:F2}"
                    );
                }

                if (!_armed)
                {
                    LogFile.Write(
                        $"[{symbol}] EquityProtection hold: not armed | " +
                        $"equity={currentEquity:F2}, start={_startEquity:F2}, max={_maxEquity:F2}, " +
                        $"profitFromStart={profitFromStart:F2}, armThreshold={armThreshold:F2}"
                    );

                    return false;
                }

                if ((currentCandleTime - _armedAtCandle).TotalHours < profile.MinCandlesAfterArm)
                {
                    LogFile.Write(
                        $"[{symbol}] EquityProtection hold: minimum candles after arm not reached | " +
                        $"armedAt={_armedAtCandle}, current={currentCandleTime}"
                    );

                    return false;
                }

                double allowedGiveback =
                    Math.Max(
                        atrUsdEquivalent * profile.GivebackAtrMultiplier,
                        profile.MinGivebackUsd
                    );

                // Symbol-adaptive strong trend breathing.
                // Instead of using fixed atrUsdEquivalent >= 20,
                // this checks whether the equity run has expanded beyond normal activation.
                bool strongTrendActive =
                    profitFromStart >= armThreshold * 2.0;

                if (strongTrendActive)
                    allowedGiveback *= profile.StrongTrendBreathingFactor;

                double closeLine = _maxEquity - allowedGiveback;

                LogFile.Write(
                    $"[{symbol}] EquityProtection eval | " +
                    $"equity={currentEquity:F2}, start={_startEquity:F2}, max={_maxEquity:F2}, " +
                    $"profitFromStart={profitFromStart:F2}, atrUsdEq={atrUsdEquivalent:F2}, " +
                    $"armed={_armed}, armThreshold={armThreshold:F2}, " +
                    $"allowedGiveback={allowedGiveback:F2}, closeLine={closeLine:F2}, " +
                    $"strongTrend={strongTrendActive}"
                );

                if (currentEquity <= closeLine)
                {
                    LogFile.Write(
                        $"[{symbol}] EQUITY PROTECTION EXIT | " +
                        $"equity={currentEquity:F2}, max={_maxEquity:F2}, " +
                        $"giveback={(_maxEquity - currentEquity):F2}, allowed={allowedGiveback:F2}"
                    );

                    return true;
                }

                return false;
            }
        }

        private static Profile GetProfile(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            if (IsGold(s))
            {
                return new Profile
                {
                    ArmAtrMultiplier = 0.55,
                    GivebackAtrMultiplier = 0.55,
                    MinArmUsd = 12.0,
                    MinGivebackUsd = 7.0,
                    StrongTrendBreathingFactor = 1.20,
                    MinCandlesAfterArm = 1
                };
            }

            if (IsCrypto(s))
            {
                return new Profile
                {
                    ArmAtrMultiplier = 0.60,
                    GivebackAtrMultiplier = 0.60,
                    MinArmUsd = 10.0,
                    MinGivebackUsd = 7.0,
                    StrongTrendBreathingFactor = 1.25,
                    MinCandlesAfterArm = 1
                };
            }

            if (IsIndex(s))
            {
                return new Profile
                {
                    ArmAtrMultiplier = 0.65,
                    GivebackAtrMultiplier = 0.65,
                    MinArmUsd = 12.0,
                    MinGivebackUsd = 8.0,
                    StrongTrendBreathingFactor = 1.25,
                    MinCandlesAfterArm = 1
                };
            }

            // FX default
            return new Profile
            {
                ArmAtrMultiplier = 0.50,
                GivebackAtrMultiplier = 0.55,
                MinArmUsd = 8.0,
                MinGivebackUsd = 5.0,
                StrongTrendBreathingFactor = 1.15,
                MinCandlesAfterArm = 1
            };
        }

        private static bool IsGold(string symbol)
        {
            return symbol.Contains("XAU") ||
                   symbol.Contains("XAG") ||
                   symbol.Contains("GOLD") ||
                   symbol.Contains("SILVER");
        }

        private static bool IsCrypto(string symbol)
        {
            return symbol.Contains("BTC") ||
                   symbol.Contains("ETH") ||
                   symbol.Contains("SOL") ||
                   symbol.Contains("AVAX") ||
                   symbol.Contains("BNB") ||
                   symbol.Contains("XRP") ||
                   symbol.Contains("ADA") ||
                   symbol.Contains("DOGE") ||
                   symbol.Contains("LTC");
        }

        private static bool IsIndex(string symbol)
        {
            return symbol.Contains("US100") ||
                   symbol.Contains("NAS") ||
                   symbol.Contains("USTEC") ||
                   symbol.Contains("US30") ||
                   symbol.Contains("DJ") ||
                   symbol.Contains("SPX") ||
                   symbol.Contains("US500") ||
                   symbol.Contains("GER") ||
                   symbol.Contains("DAX") ||
                   symbol.Contains("UK100");
        }
    }
}
