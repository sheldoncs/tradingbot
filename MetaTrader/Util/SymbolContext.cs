using MetaTrader.Data.Entities;
using MetaTrader.Enums;
using MetaTrader.helper;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;

namespace MetaTrader.Util
{
    public class SymbolContext
    {
        public bool ImpulseTradeWon { get; set; }
        public bool IsImpulse { get; set; }
        public DateTime LastImpulseWinTime { get; set; }
        public bool ImpulseConsumed { get; set; }   
        public bool NearSupport { get; set; }
        public bool NearResistance { get; set; }
        public bool IsBreakingSupport { get; set; }
        public bool IsBreakingResistance { get; set; }
        // Optional but VERY useful
        public double SupportLevel { get; set; }
        public double ResistanceLevel { get; set; }

        public int SupportTouches { get; set; }
        public int ResistanceTouches { get; set; }

        public double CurrentMomentum { get; set; }
        public double PreviousMomentum { get; set; }
        public bool InRecoveryMode { get; set; }
        public DateTime LastLossCheckCandleTime { get; set; } = DateTime.MinValue;
        public DateTime RecoveryStartedUtc { get; set; }
        public int RecoveryMinClosedCandles { get; set; } = 1; // or 2
        public int ClosedCandlesSinceRecovery { get; set; }
        public bool HasRecovered { get; set; } = false;
        public MarketPhaseLoop Phase { get; set; } = MarketPhaseLoop.Cooling;
        public DateTime? LastShockCandleTime { get; set; }
        public int CandlesSinceShock { get; set; }
        public DateTime LastPhaseUpdateCandleTime { get; set; }
        public string PhaseReason { get; set; } = "";
        public string Symbol { get; }
        public double PrevAtr { get; set; } = 0;
        public int AtrDownCount { get; set; } = 0;
        public bool ShockActive { get; set; }
        public DateTime? LastShockTime { get; set; }
        public bool M15StructureReady { get; set; }
        // --- Market State ---
        public MarketState PreviousState { get; private set; } = MarketState.Neutral;
        public MarketState State { get; private set; } = MarketState.Neutral;

        // --- Momentum / regime tracking ---
        public int ExpansionCandles { get; private set; }
        public int PullbackCandles { get; private set; }
        public double PeakAtr { get; set; } = 0.0;
        public bool PeakAtrCaptured { get; set; } = false;
        public double LastHybridMomentum { get; private set; }
        public double LastEmaDistance { get; private set; }
        public DateTime LastProcessedCandleTime { get; set; } = DateTime.MinValue;
        public DateTime LastUpdateTime { get; private set; }
        public OrderBlock ActiveOrderBlock { get; set; }
        public double ArmedHybrid { get; set; }
        public DateTime? OrderBlockCreatedAt { get; set; }
        public DateTime StateEnteredAt { get; set; } = DateTime.MinValue;

        // === GATING FLAGS (NEW) ===
        public bool PriceAccepted { get; set; }
        public bool AtrCooldown { get; set; }
        private List<string> ListOfStates = new List<string>();
        public bool IsArmed => State == MarketState.Armed;
        public double AtrPercentile { get; set; } = 50.0;
        public bool LastImpulseHadAbsorption { get; set; }
        public int CandlesSinceImpulse { get; set; }
        public bool IsSecondImpulse => LastImpulseHadAbsorption && CandlesSinceImpulse >= 2;
        public Queue<double> AtrHistory { get; } = new Queue<double>();
        public bool IsShockActive { get; set; }
        public bool AllowSniperNextCandle { get; set; }

        public SymbolContext(string symbol)
        {
            Symbol = symbol;
        }

        // --- State transition helper ---
        public void TransitionTo(MarketState newState)
        {
            if (State != newState)
            {
                State = newState;
                ListOfStates.Add(newState.ToString());
                StateEnteredAt = DateTime.UtcNow;
            }

            if (State == newState)
                return;

            PreviousState = State;
            State = newState;

            LogStateTransition();

            // Reset counters on meaningful transitions
            if (newState == MarketState.Impulse)
                ExpansionCandles = 0;

            if (newState == MarketState.Pullback)
                PullbackCandles = 0;
        }
        public void ClearListOfStates()
        {
            ListOfStates.Clear();
        }

        public List<string> GetListOfStates()
        {
            return ListOfStates;
        }
        private void LogStateTransition()
        {
            string msg = $"{Symbol}: {PreviousState} → {State}";
            //Console.WriteLine(msg);
            //LogFile.Write(msg);
        }

        // --- Called every candle ---
        public void UpdateMetrics(double hybridMomentum, double emaDistance)
        {
            LastHybridMomentum = hybridMomentum;
            LastEmaDistance = emaDistance;
            LastUpdateTime = DateTime.UtcNow;
        }

        // --- Counters ---
        public void IncrementExpansion()
        {
            ExpansionCandles++;
        }

        public void IncrementPullback()
        {
            PullbackCandles++;
        }

        public void ResetExpansion()
        {
            ExpansionCandles = 0;
        }

        public void ResetPullback()
        {
            PullbackCandles = 0;
        }
        public void MarketCoolingState()
        {
            TradingEventBus.Decision(new DecisionEvent
            {
                Symbol = Symbol,
                Action = TradeAction.Cooling.ToString(),
                Category = DecisionCategory.Cascade.ToString(),
                Reason = $"Symbol {Symbol} disarmed",
                Message = $"Symbol  {Symbol}  disarmed",
                Momentum =0.00,
                EarlyMomentum = false,
                EmaCompressed = false,
                Regime = DecisionCategory.Cooldown.ToString(),
                Time = DateTime.UtcNow
            });
        }
        public void StartRecovery()
        {
            if (InRecoveryMode)
                return;

            InRecoveryMode = true;
            RecoveryStartedUtc = DateTime.UtcNow;
            ClosedCandlesSinceRecovery = 0;
        }
        // --- Convenience flags ---
        public bool IsLateExpansion => ExpansionCandles >= 3;
        public bool PullbackSufficient => PullbackCandles >= 2;
    }

}
