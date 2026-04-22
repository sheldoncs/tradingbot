using MetaTrader.Data.Entities;
using MetaTrader.Enums.MetaTrader.Enums;
using MetaTrader.Services.MetaTrader.Services;

namespace MetaTrader.Util
{
    public static class SlTpPriceBuilder
    {
        public static (double sl, double tp) BuildPrices(
            TradeSide direction,
            double entryPrice,
            double slDistance,
            double tpDistance)
        {
            double sl;
            double tp;

            if (direction == TradeSide.Buy)
            {
                sl = entryPrice - slDistance;
                tp = entryPrice + tpDistance;
            }
            else // SELL
            {
                sl = entryPrice + slDistance;
                tp = entryPrice - tpDistance;
            }

            return (sl, tp);
        }

        // Overload for your SlTpResult
        public static (double sl, double tp) BuildPrices(
            TradeSide direction,
            double entryPrice,
            SlTpResult sltp)
        {
            return BuildPrices(
                direction,
                entryPrice,
                sltp.StopLossPriceDistance,
                sltp.TakeProfitPriceDistance
            );
        }
    }
}
