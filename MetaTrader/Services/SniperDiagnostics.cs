using MetaTrader.Util;
using System;
using System.Collections.Generic;

namespace MetaTrader.Services
{
    public static class SniperDiagnostics
    {
        private static readonly Dictionary<string, int> _blocks = new();

        public static void Count(string reason)
        {
            if (!_blocks.ContainsKey(reason))
                _blocks[reason] = 0;

            _blocks[reason]++;
        }

        public static void Print()
        {
            foreach (var kv in _blocks)
            {
                Console.WriteLine($"SNIPER BLOCKED: {kv.Key} = {kv.Value}");
                LogFile.Write($"SNIPER BLOCKED: {kv.Key} = {kv.Value}");
            }
        }
    }
}
