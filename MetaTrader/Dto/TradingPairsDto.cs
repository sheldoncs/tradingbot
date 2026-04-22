namespace MetaTrader.Dto
{
    public class TradingPairsDto
    {
        public string pair { get; set; }
        public int stoplosspoints { get; set; }
        public int takeprofitpoints { get; set; }
        public int contractsize { get; set; }
        public decimal pipvalue { get; set; }
        public decimal riskpertrade { get; set; }
        public decimal atrfactor { get; set; }
        public decimal mfactor { get; set; }
        public decimal extfactor { get; set; }
        public decimal addatrfactor { get; set; }
        public decimal profitusdbreakeven { get; set; }
        public decimal profitusdtightenstop { get; set; }
        public decimal lossusdclosetrade { get; set; }
    }
}
