using MetaTrader.Util;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace MetaTrader.Services
{
    public static class TradingEconomicsApi
    {
        private static readonly HttpClient _http = new HttpClient();

        public static async Task<List<TEEvent>> FetchUsdHighImpactNews(string apiKey)
        {
            string url =
                $"https://api.tradingeconomics.com/calendar?country=united states&importance=3&client={apiKey}";

            var data = await _http.GetFromJsonAsync<List<TEEvent>>(url);

            return data ?? new List<TEEvent>();
        }
    }

}
