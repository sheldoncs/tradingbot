namespace MetaTrader.Services
{
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;

    public static class ConnectionService
    {
        private static readonly ConcurrentDictionary<string, byte> _connections = new();

        public static IReadOnlyCollection<string> GetAll()
            => _connections.Keys.ToList();

        public static void Add(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
                _connections.TryAdd(id, 0);
        }

        public static void Remove(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
                _connections.TryRemove(id, out _);
        }

        public static bool HasAny() => !_connections.IsEmpty;
    }

}
