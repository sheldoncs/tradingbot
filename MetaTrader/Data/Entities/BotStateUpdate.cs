using MetaTrader.Enums;
using System;
using System.Collections.Generic;
using Telegram.Bot.Types;

namespace MetaTrader.Data.Entities
{
    public class BotStateUpdate
    {
        public string Symbol { get; set; }
        public string state { get; set; }
        public string Regime { get; set; }
        public double Momentum { get; set; }
        public bool EmaCompressed { get; set; }
        public DateTime Time { get; set; }
        public String Action { get; set; }
        public string Reason { get; set; }
        public double ContractSize { get; set; }
        public TradeSide Side { get; set; }
        public double score { get; set; }
        public bool PullbackSufficient { get; set; }
        public bool AtrCoolDown { get; set; }
        public bool PriceAccepted { get; set; }
        public List<string> ListOfStates { get; set; }


    }

}
