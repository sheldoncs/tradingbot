using MetaTrader.Data.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace MetaTrader.Services
{
    public static class Mt5Registry
    {
        private static readonly ConcurrentDictionary<string, Mt5Connection> _connections = new();
        
        public static void Register(string connectionId, Mt5Handshake h)
        {
            _connections.TryAdd(connectionId, new Mt5Connection
            {
                ConnectionId = connectionId,
                Account = h.Account,
                Broker = h.Broker,
                Magic = h.Magic,
                ConnectedAt = DateTime.UtcNow
            });
        }

        public static void Remove(string connectionId)
        {
            _connections.TryRemove(connectionId, out Mt5Connection value);
        }

        public static Mt5Connection? Get(string connectionId)
            => _connections.TryGetValue(connectionId, out var c) ? c : null;
    }

}
