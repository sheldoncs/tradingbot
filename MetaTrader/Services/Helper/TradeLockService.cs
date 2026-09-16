using StackExchange.Redis;
using System;

namespace MetaTrader.Services.Helper
{
    public static class TradeLockService
    {
        private static ConnectionMultiplexer _redis;
        private static IDatabase _db;

        private const string LockKey = "sentinelfx:active-trade-lock";

        public static void Initialize(string connectionString = "localhost:6379")
        {
            _redis = ConnectionMultiplexer.Connect(connectionString);
            _db = _redis.GetDatabase();
        }

        public static bool TryAcquire(string symbol, string engine, TimeSpan? expiry = null)
        {
            expiry ??= TimeSpan.FromHours(4);

            string value = $"{symbol}|{engine}|{DateTime.UtcNow:O}";

            return _db.StringSet(
                LockKey,
                value,
                expiry,
                When.NotExists
            );
        }

        public static void Release()
        {
            _db.KeyDelete(LockKey);
        }

        public static bool IsLocked()
        {
            return _db.KeyExists(LockKey);
        }

        public static string GetOwner()
        {
            return _db.StringGet(LockKey);
        }
    }
}
