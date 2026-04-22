using System;

namespace MetaTrader.Data.Entities
{
    public class Mt5Connection
    {
        public string ConnectionId { get; set; }
        public long Account { get; set; }
        public string Broker { get; set; }
        public int Magic { get; set; }
        public DateTime ConnectedAt { get; set; }
    }
}
