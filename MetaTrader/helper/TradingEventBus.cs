using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using System;

namespace MetaTrader.helper
{
    public static class TradingEventBus
    {
        public static event Action<string>? OnLog;
        public static event Action<BotStateUpdate>? OnState;
        public static event Action<Trade>? OnTrade;
        public static event Action<TradeAction>? OnTradeAction;
        public static event Action<DecisionEvent> OnDecision;
        public static event Action<SniperEvent> onSniper;
        public static event Action<PerformanceContext> onPerformanceContext;
        public static event Action<M15Snapshot> onM15Snapshot;
        public static event Action<MarketPhaseLoop> onMarketPhaseLoop;

        public static void Log(string message)
        {
            OnLog?.Invoke(message);
        }

        public static void Sniper(SniperEvent snipe)
        {
            onSniper?.Invoke(snipe);
        } 
        public static void State(BotStateUpdate state)
        {
            OnState?.Invoke(state);
        }

        public static void ActiveTrade(Trade trade)
        {
            OnTrade?.Invoke(trade);
        }
        public static void TradeActionDecision(TradeAction tradeAction)
        {
            OnTradeAction?.Invoke(tradeAction);
        }
        public static void Decision(DecisionEvent decision)
        {
            OnDecision?.Invoke(decision);
        }

        public static void Performance(PerformanceContext pct)
        {
            onPerformanceContext?.Invoke(pct);
        }

        public static void M15SnapshotUpdate(M15Snapshot snapshot)
        {
            onM15Snapshot?.Invoke(snapshot);
        }
        public static void MarketPhaseUpdate(MarketPhaseLoop mphase)
        {
            onMarketPhaseLoop?.Invoke(mphase);
        }
    }
}
