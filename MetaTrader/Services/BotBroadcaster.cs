using chatgptbot.Util;
using MetaTrader.Services.Contract;
using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace MetaTrader.Services
{
    public class BotBroadcaster : IBotBroadcaster
    {
        private readonly IHubContext<ChatHub> _hub;

        public BotBroadcaster(IHubContext<ChatHub> hub)
        {
            _hub = hub;
        }

        public Task Log(string message) =>
            _hub.Clients.All.SendAsync("OnLog", message);

        public Task State(object stateDto) =>
            _hub.Clients.All.SendAsync("OnState", stateDto);

        public Task Trade(object tradeDto) =>
            _hub.Clients.All.SendAsync("OnTrade", tradeDto);
    }
}
