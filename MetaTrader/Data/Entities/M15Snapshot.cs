using System;
using Telegram.Bot.Types;

namespace MetaTrader.Data.Entities
{
    public sealed class M15Snapshot
    {
        public string Symbol { get; init; }
        public bool Bullish { get; init; }
        public bool Bearish { get; init; }
        public string Message {get; set; }
        public DateTime CandleTime { get; init; }

        public double Atr { get; set; }

        public double EmaFast { get; set; }       // EMA 9
        public double EmaSlow { get; set; }       // EMA 21
        public double PrevEmaFast { get; set; }   // for slope

        public bool IsImpulsive { get; set; }

    }

}
