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
        private const int MAX_CONCURRENT_TRADES = 2;
        private const int LOCK_TIMEOUT_MINUTES = 10;

        // --------------------------------
        // 🔒 TRY ACQUIRE LOCK
        // --------------------------------
        public static bool TryAcquireTradeLock(IServiceProvider provider)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            var now = DateTime.UtcNow;

            var lockRow = db.GlobalTradeLock.FirstOrDefault(x => x.Id == LOCK_ID);

            // ✅ Create ONLY if not exists
            if (lockRow == null)
            {
                lockRow = new GlobalTradeLock
                {
                    Id = LOCK_ID,
                    IsLockedCntr = 0,
                    LockedAt = now
                };

                db.GlobalTradeLock.Add(lockRow);
                db.SaveChanges();
            }

            // 🧹 Reset stale lock
            if (lockRow.LockedAt.HasValue)
            {
                var diff = now - lockRow.LockedAt.Value;

                if (diff.TotalMinutes > LOCK_TIMEOUT_MINUTES)
                {
                    lockRow.IsLockedCntr = 0;
                }
            }

            // 🚫 Block if max reached
            if (lockRow.IsLockedCntr >= MAX_CONCURRENT_TRADES)
            {
                return false;
            }

            // ➕ Increment safely
            lockRow.IsLockedCntr += 1;
            lockRow.LockedAt = now;

            db.SaveChanges();

            return true;
        }

        // --------------------------------
        // 🔓 RELEASE LOCK
        // --------------------------------
        public static void ReleaseTradeLock(IServiceProvider provider)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            var lockRow = db.GlobalTradeLock.FirstOrDefault(x => x.Id == LOCK_ID);

            if (lockRow == null)
                return;

            // ➖ Decrement safely
            lockRow.IsLockedCntr = Math.Max(0, lockRow.IsLockedCntr - 1);
            lockRow.LockedAt = DateTime.UtcNow;

            db.SaveChanges();
        }

        public static void ReleaseAllTradeLock(IServiceProvider provider)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            var lockRow = db.GlobalTradeLock.FirstOrDefault(x => x.Id == LOCK_ID);

            if (lockRow == null)
                return;

            // ➖ Decrement safely
            lockRow.IsLockedCntr = 0;
            lockRow.LockedAt = DateTime.UtcNow;

            db.SaveChanges();
        }
        // --------------------------------
        // 📊 GET ACTIVE TRADES
        // --------------------------------
        public static int GetActiveTrades(IServiceProvider provider)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            var lockRow = db.GlobalTradeLock.FirstOrDefault(x => x.Id == LOCK_ID);

            return lockRow?.IsLockedCntr ?? 0;
        }

        // --------------------------------
        // 🚫 CHECK MAX LIMIT
        // --------------------------------
        public static bool HasReachedMaxTrades(IServiceProvider provider)
        {
            return GetActiveTrades(provider) >= MAX_CONCURRENT_TRADES;
        }
    }
}
