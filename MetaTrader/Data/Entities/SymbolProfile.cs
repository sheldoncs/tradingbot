namespace MetaTrader.Data.Entities
{
    public class SymbolProfile
    {
        public double WeakStructureThreshold { get; set; }
        public double ExtensionFactor { get; set; }

        // (Optional — future expansion)
        public double MinAtrPercentile { get; set; }
        public double MaxSpreadAtrFactor { get; set; }
    }
}
