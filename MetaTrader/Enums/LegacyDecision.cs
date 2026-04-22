namespace MetaTrader.Enums
{
   
    public enum LegacyDecision
    {
        StructuredOnly,        // Sniper + Velocity only
        StructuredWithLegacy,  // Sniper + Velocity + Legacy fallback
        LegacyOnly             // Old behaviour
    }

}
