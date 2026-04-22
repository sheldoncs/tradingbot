using MetaTrader.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MetaTrader.Services
{
    public static class LiveNewsFilter
    {
        private static DateTime _lastFetch = DateTime.MinValue;
        private static List<DateTime> _cachedNewsTimes = new();

        private static readonly TimeSpan Pre = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan Post = TimeSpan.FromMinutes(30);

        private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);

        public static async Task<bool> IsNewsNow(DateTime nowUtc, string apiKey)
        {
            if (nowUtc - _lastFetch > RefreshEvery)
            {
                var events = await TradingEconomicsApi.FetchUsdHighImpactNews(apiKey);

                _cachedNewsTimes = events
                    .Where(e => e.Date.HasValue)
                    .Select(e => DateTime.SpecifyKind(e.Date.Value, DateTimeKind.Utc))
                    .ToList();

                _lastFetch = nowUtc;
            }

            foreach (var newsTime in _cachedNewsTimes)
            {
                if (nowUtc >= newsTime - Pre && nowUtc <= newsTime + Post)
                    return true;
            }

            return false;
        }
    }

}


