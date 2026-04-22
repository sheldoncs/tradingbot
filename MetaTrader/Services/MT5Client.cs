using MetaTrader.Data.Entities;
using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MetaTrader.Services
{
    public class MT5Client : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;

        public async Task ConnectAsync(string host, int port)
        {
            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();
            Console.WriteLine("Connected to MT5.");
        }

        public async Task<string> SendCommandAsync(MT5Command command)
        {
            string json = JsonSerializer.Serialize(command) + "\n";
            byte[] data = Encoding.UTF8.GetBytes(json);
            await _stream.WriteAsync(data, 0, data.Length);

            byte[] buffer = new byte[4096];
            int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }

        public async Task<MT5PriceUpdate> SubscribePriceAsync(string symbol)
        {
            var subscribeCmd = new MT5Command
            {
                type = "subscribe",
                symbol = symbol
            };

            string response = await SendCommandAsync(subscribeCmd);
            return JsonSerializer.Deserialize<MT5PriceUpdate>(response);
        }

        public async Task<string> CloseOrderAsync(string symbol, string side)
        {
            var closeCmd = new MT5Command
            {
                type = "order",
                symbol = symbol,
                side = side == "buy" ? "sell" : "buy",
                volume = 0
            };

            return await SendCommandAsync(closeCmd);
        }

        public void Dispose()
        {
            _stream?.Close();
            _client?.Close();
        }
    }
}
