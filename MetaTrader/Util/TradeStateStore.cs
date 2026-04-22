namespace MetaTrader.Util
{
    using MetaTrader.Data.Entities;
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public class TradeStateStore
    {
        private static readonly SemaphoreSlim _lock = new(1, 1);

        private static readonly string StateFilePath =
            Path.Combine(AppContext.BaseDirectory, "trade-state.json");

        public async Task SaveAsync(bool autoTrade)
        {
            await _lock.WaitAsync();
            try
            {
                var state = new TradeState
                {
                    AutoTrade = autoTrade
                };

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(StateFilePath, json);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveConnectionId(string connectionId)
        {
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(StateFilePath))
                    return;

                var json = await File.ReadAllTextAsync(StateFilePath);
                TradeState ts = JsonSerializer.Deserialize<TradeState>(json);

                var state = new TradeState
                {
                    AutoTrade = ts.AutoTrade,
                    connectionId = connectionId
                };

                var jsonUpdate = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(StateFilePath, jsonUpdate);

            } finally
            {
                _lock.Release();
            }

        }
        public async Task<TradeState?> LoadAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(StateFilePath))
                    return null;

                var json = await File.ReadAllTextAsync(StateFilePath);
                return JsonSerializer.Deserialize<TradeState>(json);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task ClearAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (File.Exists(StateFilePath))
                    File.Delete(StateFilePath);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

}
