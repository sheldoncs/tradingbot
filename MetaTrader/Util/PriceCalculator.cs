using MtApi5;

namespace MetaTrader.Util
{
    public static class PriceCalculator
    {
        public static double GetBid(MtApi5Client client, string symbol)
        {
            return client.SymbolInfoDouble(
                symbol,
                ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_BID
            );
        }

        public static double GetAsk(MtApi5Client client, string symbol)
        {
            return client.SymbolInfoDouble(
                symbol,
                ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_ASK
            );
        }

        public static double GetMid(double bid, double ask)
        {
            return (bid + ask) / 2.0;
        }

        public static (double Bid, double Ask, double Mid) GetPrices(
            MtApi5Client client,
            string symbol
        )
        {
            double bid = GetBid(client, symbol);
            double ask = GetAsk(client, symbol);
            double mid = GetMid(bid, ask);

            return (bid, ask, mid);
        }
    }

}
