using MtApi5;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Util
{
    public class PositionManager
    {
        private readonly MtApi5Client _client;

        public PositionManager(MtApi5Client client)
        {
            _client = client;
        }

        public bool HasOpenPosition()
        {

            List<long> mt5Tickets = GetAllOpenPositionTickets();

            long ticket = mt5Tickets.Select(s => s).SingleOrDefault();

            bool positions = _client.PositionSelectByTicket((ulong)ticket);

            return positions;
        }

        public List<long> GetAllOpenPositionTickets()
        {
            var tickets = new List<long>();
            int total = _client.PositionsTotal();

            for (int i = 0; i < total; i++)
            {
                ulong ticketU = _client.PositionGetTicket(i);
                long ticket = (long)ticketU;
                tickets.Add(ticket);
            }

            return tickets;
        }
    }

}
