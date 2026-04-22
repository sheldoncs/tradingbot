using MetaTrader.Data.Entities;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MetaTrader.Services
{
    public static class Mt5Gateway 
    {
        private static TcpListener _listener;
        private static readonly ConcurrentDictionary<long, TcpClient> _clients = new();
        public static Mt5Handshake handShake => hshake;
        private static Mt5Handshake hshake;
        public static async Task StartAsync(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            Console.WriteLine($"MT5 Gateway listening on {port}");
            var client = await _listener.AcceptTcpClientAsync();
            _ = Task.Run(() => Mt5Gateway.HandleMt5ClientAsync(client));

        }

        public static async Task HandleMt5ClientAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            string connectionId = Guid.NewGuid().ToString();

            try
            {
                // 1️⃣ Handshake
                string json = await reader.ReadLineAsync();
                var handshake = JsonSerializer.Deserialize<Mt5Handshake>(json);
                hshake = handShake;

                if (!IsValidHandshake(handshake))
                {
                    await writer.WriteLineAsync("REJECT");
                    client.Close();
                    return;
                }

                Mt5Registry.Register(connectionId, handshake);
                await writer.WriteLineAsync("ACCEPT");

                Console.WriteLine($"MT5 connected: {handshake.Account}");

                // 2️⃣ Message loop
                while (client.Connected)
                {
                    string msg = await reader.ReadLineAsync();
                    if (msg == null) break;

                    HandleMt5Message(connectionId, msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MT5 error: {ex.Message}");
            }
            finally
            {
                Mt5Registry.Remove(connectionId);
                client.Close();
            }
        }

        private static bool IsValidHandshake(Mt5Handshake h)
        {
            return h != null
                && h.Account > 0
                && h.Token == "SFX_SHARED_SECRET";
        }

        private static void HandleMt5Message(string connectionId, string msg)
        {
            var mt5 = Mt5Registry.Get(connectionId);
            if (mt5 == null) return;

            Console.WriteLine($"[{mt5.Account}] {msg}");
        }

        // =========================
        // Send command to MT5
        // =========================
        public static void SendCommand(long account, object command)
        {
            if (!_clients.TryGetValue(account, out var client))
                throw new InvalidOperationException($"No MT5 connected for account {account}");

            if (!client.Connected)
                throw new InvalidOperationException($"MT5 disconnected for account {account}");

            var json = JsonSerializer.Serialize(command) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);

            client.GetStream().Write(bytes, 0, bytes.Length);
        }

        // =========================
        // Helpers
        // =========================
        private static void SendRaw(TcpClient client, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            client.GetStream().Write(bytes, 0, bytes.Length);
        }

        private static bool ValidateToken(string token)
        {
            // TODO: replace with DB lookup
            return !string.IsNullOrWhiteSpace(token);
        }

    }

}
