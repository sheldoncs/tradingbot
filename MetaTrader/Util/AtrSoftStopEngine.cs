using MetaTrader.Data.Entities;

namespace MetaTrader.Util
{
    public static class AtrSoftStopEngine
    {
        public const double ATR_FACTOR = 0.50; // Gold safe default

        public static bool ShouldExit(
            TrackedTrade trade,
            double bid,
            double ask,
            double atr)
        {
            if (trade == null || atr <= 0)
                return false;

            double adverse =
                trade.Side == TradeSide.Buy
                    ? (trade.EntryPrice - bid)
                    : (ask - trade.EntryPrice);

            return adverse >= atr * ATR_FACTOR;
        }
    }


}
