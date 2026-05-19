namespace MetaTrader.Data.Entities
{
    public class MarketConditionSnapshot
    {
        public double BodyRatio { get; set; }

        public double RangeAtr { get; set; }
        public bool BullishCandle { get; set; }
        public bool BearishCandle { get; set; }

        public double Ema9 { get; set; }

        public double PrevEma9 { get; set; }

        public double Ema21 { get; set; }

        public double PrevEma21 { get; set; }

        public double Ema9Slope { get; set; }

        public double Ema21Slope { get; set; }

        public double EmaSpread { get; set; }

        public bool TrendUp { get; set; }

        public bool TrendDown { get; set; }

        public double LastClose { get; set; }

        public double PrevClose { get; set; }

        public double CandleRange { get; set; }

        public double CandleBody { get; set; }

        public bool EmaCompression { get; set; }

        public bool SlopeWeakening { get; set; }
    }
}
