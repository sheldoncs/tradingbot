using MtApi5;

namespace MetaTrader.Util
{
    public static class ExecutionGuards
    {
        public static bool IsSpreadTooWide(
            MtApi5Client client,
            string symbol,
            double atr,
            double maxAtrRatio = 0.25)
        {
            try
            {
                // Get spread in points
                long spreadPoints = client.SymbolInfoInteger(
                    symbol,
                    ENUM_SYMBOL_INFO_INTEGER.SYMBOL_SPREAD);

                // Get point size
                double point = client.SymbolInfoDouble(
                    symbol,
                    ENUM_SYMBOL_INFO_DOUBLE.SYMBOL_POINT);

                double spreadInPrice = spreadPoints * point;

                // Compare spread vs ATR
                if (spreadInPrice > atr * maxAtrRatio)
                    return true;

                return false;
            }
            catch
            {
                // Fail-safe: if data cannot be retrieved, block entry
                return true;
            }
        }
    }

}
