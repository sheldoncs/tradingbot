using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace chatgptbot.Util
{
    public class ChatHub : Hub
    {
        // You can call this method from backend to send assistant output
        public async Task SendMessage(string message)
        {
            await Clients.All.SendAsync("BuySellIndicator", message);
        }
        public Task<string> GetConnectionId()
        {
            return Task.FromResult(Context.ConnectionId);
        }
        /*
        public override async Task OnConnectedAsync()
        {
            Console.WriteLine($"Client connected: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }*/

        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "test");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, "test");
            await base.OnDisconnectedAsync(ex);
        }


    }
}
