namespace MetaTrader.Util
{
    public class FairValueGap
    {
        /// <summary>
        /// Upper boundary of the FVG (the higher price of the gap)
        /// </summary>
        public double High { get; set; }

        /// <summary>
        /// Lower boundary of the FVG (the lower price of the gap)
        /// </summary>
        public double Low { get; set; }

        /// <summary>
        /// The index of the first candle in the FVG pattern (usually C1)
        /// </summary>
        public int StartIndex { get; set; }

        /// <summary>
        /// The index of the third candle (C3) that finalizes the FVG
        /// </summary>
        public int EndIndex { get; set; }

        /// <summary>
        /// True if it's a bullish FVG (gap up), false if bearish (gap down)
        /// </summary>
        public bool IsBullish { get; set; }

        public override string ToString()
        {
            return $"{(IsBullish ? "Bullish" : "Bearish")} FVG from {Low} to {High} (candles {StartIndex} to {EndIndex})";
        }
    }

}
