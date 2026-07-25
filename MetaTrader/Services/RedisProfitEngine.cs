namespace MetaTrader.Services
{
    using StackExchange.Redis;
    using System;
    using System.Threading.Tasks;

    public class RedisProfitEngine
    {
        private readonly IDatabase _db;

        public RedisProfitEngine(IConnectionMultiplexer redis)
        {
            _db = redis.GetDatabase();
        }

        private string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

        private string ProfitKey => $"trading:profit:{Today}";
        private string MaxProfitKey => $"trading:profit:max:{Today}";
        private string LockKey => $"trading:lock:{Today}";

        // ----------------------------------------
        // 🔹 Add profit when trade closes
        // ----------------------------------------
        public async Task AddClosedProfit(double profit)
        {
            await _db.StringIncrementAsync(ProfitKey, profit);

            var current = await GetDailyProfit();
            var max = await GetMaxProfit();

            if (current > max)
            {
                await _db.StringSetAsync(MaxProfitKey, current);
            }

            await SetExpiry();
        }

        // ----------------------------------------
        // 🔹 Get total daily profit
        // ----------------------------------------
        public async Task<double> GetDailyProfit()
        {
            var value = await _db.StringGetAsync(ProfitKey);
            return value.HasValue ? (double)value : 0;
        }

        // ----------------------------------------
        // 🔹 Get max daily profit
        // ----------------------------------------
        public async Task<double> GetMaxProfit()
        {
            var value = await _db.StringGetAsync(MaxProfitKey);
            return value.HasValue ? (double)value : 0;
        }

        // ----------------------------------------
        // 🔹 Activate global lock
        // ----------------------------------------
        public async Task ActivateLock()
        {
            await _db.StringSetAsync(LockKey, "1");
            await SetExpiry();
        }

        // ----------------------------------------
        // 🔹 Check if locked
        // ----------------------------------------
        public async Task<bool> IsLocked()
        {
            var value = await _db.StringGetAsync(LockKey);
            return value.HasValue && value == "1";
        }

        // ----------------------------------------
        // 🔹 Auto-expire keys (daily reset)
        // ----------------------------------------
        private async Task SetExpiry()
        {
            var expiry = TimeSpan.FromDays(2);

            await _db.KeyExpireAsync(ProfitKey, expiry);
            await _db.KeyExpireAsync(MaxProfitKey, expiry);
            await _db.KeyExpireAsync(LockKey, expiry);
        }
    }
}
