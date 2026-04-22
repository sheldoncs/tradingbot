namespace MetaTrader.Services
{
    using global::MetaTrader.Data.Entities;
    using System;
    using System.Collections.Concurrent;

    public static class SignalStore
    {
        private static readonly ConcurrentDictionary<long, TradeInstruction> _signals
            = new ConcurrentDictionary<long, TradeInstruction>();

        public static void Set(long account, TradeInstruction instruction)
        {
            _signals[account] = instruction;
        }

        public static TradeInstruction Get(long account)
        {
            _signals.TryGetValue(account, out var instruction);
            return instruction;


        }

        public static TradeInstruction Get1(long account)
        {
            if (!_signals.TryGetValue(account, out var instruction))
                return null;

            if (instruction.CreatedUtc.AddSeconds(30) < DateTime.UtcNow)
            {
                _signals.TryRemove(account, out _);
                return null;
            }

            return instruction;
        }

        public static void Clear(long account)
        {
            _signals.TryRemove(account, out _);
        }
    }

}
