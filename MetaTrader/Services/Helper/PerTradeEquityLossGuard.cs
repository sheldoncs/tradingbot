using MetaTrader.Util;

namespace MetaTrader.Services.Helper
{
    public static class PerTradeEquityLossGuard
    {
        private static readonly object _lock = new();

        private static bool _armed;
        private static ulong _ticket;
        private static string _symbol = "";
        private static double _entryEquity;
        private static double _maxLossUsd;

        public static void Arm(
            ulong ticket,
            string symbol,
            double currentEquity)
        {
            lock (_lock)
            {
                if (_armed && _ticket == ticket)
                    return;

                _armed = true;
                _ticket = ticket;
                _symbol = symbol ?? "";
                _entryEquity = currentEquity;
                _maxLossUsd = GetMaxLossUsd(symbol);

                LogFile.Write(
                    $"[{_symbol}] PerTradeEquityLossGuard ARMED | " +
                    $"ticket={_ticket}, entryEquity={_entryEquity:F2}, " +
                    $"maxLoss={_maxLossUsd:F2}, floor={_entryEquity - _maxLossUsd:F2}"
                );
            }
        }

        public static bool ShouldClose(
            ulong ticket,
            string symbol,
            double currentEquity)
        {
            lock (_lock)
            {
                if (!_armed || ticket != _ticket)
                    return false;

                double floor = _entryEquity - _maxLossUsd;
                double loss = _entryEquity - currentEquity;

                if (currentEquity <= floor)
                {
                    LogFile.Write(
                        $"[{symbol}] 🛑 PER-TRADE EQUITY LOSS EXIT | " +
                        $"ticket={ticket}, equity={currentEquity:F2}, " +
                        $"entryEquity={_entryEquity:F2}, loss={loss:F2}, " +
                        $"maxLoss={_maxLossUsd:F2}, floor={floor:F2}"
                    );

                    return true;
                }

                return false;
            }
        }

        public static void Reset(ulong ticket)
        {
            lock (_lock)
            {
                if (_armed && ticket == _ticket)
                {
                    LogFile.Write(
                        $"[{_symbol}] PerTradeEquityLossGuard RESET | ticket={ticket}"
                    );

                    _armed = false;
                    _ticket = 0;
                    _symbol = "";
                    _entryEquity = 0;
                    _maxLossUsd = 0;
                }
            }
        }

        public static void ForceReset()
        {
            lock (_lock)
            {
                LogFile.Write(
                    $"[{_symbol}] PerTradeEquityLossGuard FORCE RESET | ticket={_ticket}"
                );

                _armed = false;
                _ticket = 0;
                _symbol = "";
                _entryEquity = 0;
                _maxLossUsd = 0;
            }
        }

        private static double GetMaxLossUsd(string symbol)
        {
            string s = symbol?.ToUpperInvariant() ?? "";

            // GOLD / METALS
            if (s.Contains("XAU") || s.Contains("XAG") || s.Contains("GOLD") || s.Contains("SILVER"))
                return 6.00;

            // BNB gets more room because of shakeouts
            if (s.Contains("BNB"))
                return 8.00;

            // SOL is sharper and should be cut faster
            if (s.Contains("SOL"))
                return 3.00;

            // ETH moderate room
            if (s.Contains("ETH"))
                return 5.00;

            // BTC needs room, but still controlled
            if (s.Contains("BTC"))
                return 8.00;

            // FX majors
            if (s.Contains("EURUSD") ||
                s.Contains("GBPUSD") ||
                s.Contains("USDJPY") ||
                s.Contains("EURJPY") ||
                s.Contains("USDCAD") ||
                s.Contains("AUDUSD"))
                return 3.00;

            // Indices / CFDs
            if (s.Contains("US100") ||
                s.Contains("NAS") ||
                s.Contains("USTEC") ||
                s.Contains("US30") ||
                s.Contains("SPX") ||
                s.Contains("US500"))
                return 5.00;

            return 4.00;
        }
    }
}
