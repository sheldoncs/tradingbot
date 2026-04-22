using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace MetaTrader.Services.Interfaces
{
    public interface INotificationService
    {
        Task<String> sendNotification(String message, string chatId, string botToken);
    }
}
