using Microsoft.EntityFrameworkCore.Metadata.Internal;
using System;
using static MetaTrader.Services.TradeServiceEngine;

namespace MetaTrader.Data.Entities
{
    public class TrackedTrade
    {
        public ulong Ticket { get; set; }
        public DateTime OpenTime { get; set; }
        public int WeakCount { get; set; }
        public string Symbol { get; set; } = "";
        public double StopLoss { get; set; }
        public TradeSide Side { get; set; }
        public double EntryPrice { get; set; }
        public double ContractSize { get; set; }
        public double TakeProfit { get; set; }
        public double ProfitUsd { get; set; }
        public double MaxProfitUsd { get; set; }
        public double InitialRiskUsd { get; set; }
        public double LotSize { get; set; }
        public double Volume { get; set; }   // lots
        public bool IsClosed { get; set; }
        public double PeakOrTrough { get; set; } // for trailing: highest price (buy) or lowest (sell) seen since entry
        public EntryModel Model { get; set; }
        public bool IsBreakEvenSet { get; set; }
        public DateTime EntryCandleTime { get; set; }
        public int CandlesSinceEntry { get; set; }
        public bool IsProven { get; set; }
        public double LastCandleBodyUsd { get; set; }

    }
}
