namespace MetaTrader.Data.Entities
{
    public class Mt5Handshake
    {
        public long Account { get; set; }
        public string Broker { get; set; }
        public int Magic { get; set; }
        public string Token { get; set; }
    }
}
