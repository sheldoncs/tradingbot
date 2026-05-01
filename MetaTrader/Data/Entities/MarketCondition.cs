namespace MetaTrader.Data.Entities
{
    public enum MarketCondition
    {
        Unknown,
        Compression,   // weak + near EMA
        Expansion,     // strong + not extended
        ExtendedTrend, // strong + extended
        Exhaustion     // weak + extended (🚨 avoid)
    }
}
