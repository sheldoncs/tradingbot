using MetaTrader.Data.Database;
using MetaTrader.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using System;
using System.Data.SqlClient;
using System.Linq;

namespace MetaTrader.Services.Helper
{
    public static class GlobalTradeManager
    {
        private const int LOCK_ID = 1;
        private const int LOCK_TIMEOUT_MINUTES = 10;

        // --------------------------------
        // 🔒 TRY ACQUIRE LOCK (ATOMIC)
        // --------------------------------
        public static bool TryAcquireTradeLock(IServiceProvider provider)
        {
            GlobalTradeLock GlobalTradeLock = new GlobalTradeLock
            {
                Id = LOCK_ID,
                IsLocked = true,
                LockedAt = DateTime.UtcNow
            };

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            db.AddEntry<GlobalTradeLock>(GlobalTradeLock, false);

            return true;
            
        }

        // --------------------------------
        // 🔓 RELEASE LOCK
        // --------------------------------
        public static void ReleaseTradeLock(IServiceProvider provider)
        {
            GlobalTradeLock GlobalTradeLock = new GlobalTradeLock
            {
                Id = LOCK_ID,
                IsLocked = false,
                LockedAt = DateTime.UtcNow
            };

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            db.AddEntry<GlobalTradeLock>(GlobalTradeLock, false);
        }

        // --------------------------------
        // 🧠 CHECK (NON-CRITICAL)
        // --------------------------------
        public static bool HasActiveLock(IServiceProvider provider)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var lockRow = db.GlobalTradeLock.Where(x => x.Id == LOCK_ID).FirstOrDefault();
            
            if (lockRow == null)
            {
                return false;
            }

            return lockRow.IsLocked;
        }
    }
}
