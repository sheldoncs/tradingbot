using System.Threading.Tasks;

namespace MetaTrader.Services.Contract
{
    public interface IBotBroadcaster
    {
        Task Log(string message);
        Task State(object stateDto);
        Task Trade(object tradeDto);
    }
}
