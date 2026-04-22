using MetaTrader.Data.Entities;

namespace MetaTrader.Services
{
    public class TrackedTrader
    {
        public long Ticket;
        public TradeSide Side;
        public double EntryPrice;
        public double StopLoss;
        public double TakeProfit;
        public double InitialSlDistance;
        public bool BreakEvenDone = false;

        public TrackedTrader(long ticket, TradeSide side, double entry, double slDist, double tpDist)
        {
            Ticket = ticket;
            Side = side;
            EntryPrice = entry;
            InitialSlDistance = slDist;
            TakeProfit = side == TradeSide.Buy
                ? entry + tpDist
                : entry - tpDist;

            StopLoss = side == TradeSide.Buy
                ? entry - slDist
                : entry + slDist;
        }
    }
}
