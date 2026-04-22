using MetaTrader.Services.Interfaces;
using MetaTrader.Util;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TradingApplication.Exceptions;

namespace MetaTrader.Services
{
    public class NotificationService : INotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _memoryCache;
        private readonly HttpClient _httpClient;
        public NotificationService(IConfiguration configuration, IMemoryCache memoryCache)
        {
            _configuration = configuration;
            _memoryCache = memoryCache;
            _httpClient = new HttpClient();
        }

        public async Task<String> sendNotification(string message, string chatId, string botToken)
        {
            try
            {
                var data = new
                {
                    chat_id = chatId,
                    text = message
                };

                String json = JsonSerializer.Serialize(data);
                HttpContent content = new StringContent(json, Encoding.UTF8, "application/json");
               
                    HttpResponseMessage response = await _httpClient.PostAsync($"https://api.telegram.org/bot{botToken}/sendMessage", content);
                    string responseString = await response.Content.ReadAsStringAsync();
                    return await Task.FromResult(responseString);

               
            } catch (Exception ex)
            {
                LogFile.Write($"Error sending notification: {ex.Message}\n{ex.StackTrace}");
                throw new NotificationException(String.Format("Error sending notification!! {0}", ex.Message));
            }

        }
    }
}
