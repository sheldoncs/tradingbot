namespace MetaTrader.Util
{
    public static class SymbolPriorityEngine
    {
        private static readonly object _lock = new();
        private static string _activeSymbol;

        public static string ActiveSymbol
        {
            get { lock (_lock) return _activeSymbol; }
        }
        private static double _activePriority;

        public static bool TryAcquire(string symbol, double priority)
        {
            lock (_lock)
            {
                if (_activeSymbol == null)
                {
                    _activeSymbol = symbol;
                    _activePriority = priority;
                    return true;
                }

                if (priority > _activePriority)
                {
                    _activeSymbol = symbol;
                    _activePriority = priority;
                    return true;
                }

                return false;
            }
        }


        public static void Release(string symbol)
        {
            lock (_lock)
            {
                if (_activeSymbol == symbol)
                    _activeSymbol = null;
            }
        }

        public static bool IsBlocked(string symbol)
        {
            lock (_lock)
            {
                return _activeSymbol != null && _activeSymbol != symbol;
            }
        }
    }


}
