namespace MetaTrader.Data.Entities
{
    public class ResistanceContext
    {
        public bool IsNear { get; set; }
        public double Distance { get; set; }
        public double Level { get; set; }
        public string Type { get; set; } // Supply / FVG / Shelf
    }

}
