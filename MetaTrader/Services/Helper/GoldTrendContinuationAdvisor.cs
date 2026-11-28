using System;
using System.Collections.Concurrent;

namespace MetaTrader.Services.Helper
{
    /// <summary>
    /// GOLD H1 stateful pullback-continuation candidate detector. No broker calls,
    /// position closures, stop modifications, or risk-limit overrides.
    /// The caller may act on WouldConsiderFreshContinuation only after all
    /// existing execution, M15, support/resistance, risk and anti-chase gates.
    /// </summary>
    public sealed class GoldTrendContinuationAdvisor
    {
        public enum Direction { Buy, Sell }
        public enum EntryLocation { Unknown, Favorable, Extended, ExhaustedShallowBreak }
        public enum TrendHealth { Unknown, Aligned, Pullback, StructuralWarning }
        public enum ContinuationState { None, PullbackObserved, PossibleResumption, ConfirmedResumption }

        public sealed class Snapshot
        {
            public string Symbol { get; set; } = "";
            public Direction Side { get; set; }
            public DateTime TimeUtc { get; set; }
            public double Bid { get; set; }
            public double Ask { get; set; }
            public double Atr { get; set; }
            public double Ema9 { get; set; }
            public double PreviousEma9 { get; set; }
            public double Ema21 { get; set; }
            public double PreviousEma21 { get; set; }
            public double CurrentH1Open { get; set; }
            public double RecentClosedHigh { get; set; }
            public double RecentClosedLow { get; set; }
            public double LastClosedOpen { get; set; }
            public double LastClosedClose { get; set; }
            public bool M15Aligned { get; set; }
            public bool HasM15Data { get; set; }
            public bool OpposingSrWallAhead { get; set; }
            public double OpposingSrClearanceAtr { get; set; }
            public bool HasOpposingSrData { get; set; }
            // Broker-verified entry economics only; zero means unknown.
            public double HardLossUsd { get; set; }
            public double TickSize { get; set; }
            public double TickValueInAccountCurrencyPerLot { get; set; }
            public double Lots { get; set; }
            public bool PositionOpen { get; set; }
            public double EntryPrice { get; set; }
        }

        public sealed class Assessment
        {
            public bool Valid { get; internal set; }
            public string Reason { get; internal set; } = "";
            public EntryLocation Location { get; internal set; }
            public TrendHealth Health { get; internal set; }
            public ContinuationState Continuation { get; internal set; }
            public bool WouldConsiderFreshContinuation { get; internal set; }
            public bool NormalPullbackPossible { get; internal set; }
            public double Ema9DistanceAtr { get; internal set; }
            public double DirectionalH1BodyAtr { get; internal set; }
            public double BreakoutAtr { get; internal set; }
            public double AllowedAdverseMoveAtr { get; internal set; } = double.NaN;
            public string RiskRoomNote { get; internal set; } = "";
        }

        private sealed class Observation
        {
            public DateTime LastUtc;
            public DateTime PullbackFirstUtc;
            public double PullbackExtreme;
            public int ResumptionSamples;
            public DateTime LastResumptionUtc;
        }

        private readonly ConcurrentDictionary<string, Observation> _observations =
            new ConcurrentDictionary<string, Observation>(StringComparer.OrdinalIgnoreCase);

        public Assessment Evaluate(Snapshot s)
        {
            var a = new Assessment { Location = EntryLocation.Unknown, Health = TrendHealth.Unknown };
            if (s == null || string.IsNullOrWhiteSpace(s.Symbol) ||
                !(s.Symbol.IndexOf("XAU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  s.Symbol.IndexOf("GOLD", StringComparison.OrdinalIgnoreCase) >= 0))
                return Invalid(a, "Not a GOLD symbol");
            if (!Finite(s.Atr) || s.Atr <= 0 || !Finite(s.Bid) || !Finite(s.Ask) ||
                s.Bid <= 0 || s.Ask < s.Bid || !Positive(s.Ema9) ||
                !Positive(s.Ema21) || !Positive(s.PreviousEma9) ||
                !Positive(s.PreviousEma21) || !Positive(s.CurrentH1Open) ||
                !Positive(s.RecentClosedHigh) || !Positive(s.RecentClosedLow) ||
                s.RecentClosedHigh < s.RecentClosedLow || s.TimeUtc == default(DateTime))
                return Invalid(a, "Missing or invalid quote/H1/EMA inputs");

            a.Valid = true;
            bool buy = s.Side == Direction.Buy;
            double sign = buy ? 1.0 : -1.0;
            double mid = (s.Bid + s.Ask) * 0.5;
            double entryExecutable = buy ? s.Ask : s.Bid;
            double exitExecutable = buy ? s.Bid : s.Ask;
            a.Ema9DistanceAtr = Math.Abs(entryExecutable - s.Ema9) / s.Atr;
            a.DirectionalH1BodyAtr = Math.Max(0, sign * (mid - s.CurrentH1Open)) / s.Atr;
            a.BreakoutAtr = (buy ? entryExecutable - s.RecentClosedHigh : s.RecentClosedLow - entryExecutable) / s.Atr;

            bool aligned = sign * (s.Ema9 - s.Ema21) > 0 &&
                           sign * (s.Ema9 - s.PreviousEma9) > 0 &&
                           sign * (s.Ema21 - s.PreviousEma21) >= 0;
            bool priceBeyond9 = sign * (exitExecutable - s.Ema9) >= 0;
            bool priceBeyond21 = sign * (exitExecutable - s.Ema21) >= 0;
            bool closedOpposite = Positive(s.LastClosedOpen) && Positive(s.LastClosedClose) &&
                                  sign * (s.LastClosedClose - s.LastClosedOpen) < 0;
            bool deep9Breach = sign * (s.Ema9 - exitExecutable) / s.Atr >= 0.15;
            bool deep21Breach = sign * (s.Ema21 - exitExecutable) / s.Atr >= 0.18;
            a.Health = aligned && priceBeyond9 && priceBeyond21 && !closedOpposite
                ? TrendHealth.Aligned
                : aligned && priceBeyond21 && !deep9Breach
                    ? TrendHealth.Pullback
                    : deep21Breach && (closedOpposite || !aligned)
                        ? TrendHealth.StructuralWarning
                        : TrendHealth.Unknown;
            a.NormalPullbackPossible = aligned && priceBeyond21 && !deep21Breach;

            bool exhaustedShallow = a.DirectionalH1BodyAtr >= 1.10 &&
                a.Ema9DistanceAtr >= 1.10 &&
                a.BreakoutAtr >= 0.05 && a.BreakoutAtr <= 0.30;
            a.Location = exhaustedShallow ? EntryLocation.ExhaustedShallowBreak
                : a.Ema9DistanceAtr >= 1.10 ? EntryLocation.Extended
                : EntryLocation.Favorable;

            if (Positive(s.HardLossUsd) && Positive(s.TickSize) &&
                Positive(s.TickValueInAccountCurrencyPerLot) && Positive(s.Lots))
            {
                double allowedPrice = s.HardLossUsd * s.TickSize /
                    (s.TickValueInAccountCurrencyPerLot * s.Lots);
                a.AllowedAdverseMoveAtr = allowedPrice / s.Atr;
                a.RiskRoomNote = "Estimated adverse ATR until monetary limit, assuming stable tick value; spread, commissions and slippage can reduce room.";
            }
            else a.RiskRoomNote = "ATR breathing room unknown: broker-verified tick size/value, lot size and hard-loss amount required.";

            // Observation is per symbol AND side; never retain a different market's state.
            string key = s.Symbol + "|" + s.Side;
            var state = _observations.GetOrAdd(key, _ => new Observation());
            lock (state)
            {
                if (state.LastUtc != default(DateTime) &&
                    (s.TimeUtc < state.LastUtc || s.TimeUtc - state.LastUtc > TimeSpan.FromMinutes(15)))
                {
                    state.PullbackFirstUtc = default(DateTime);
                    state.PullbackExtreme = 0;
                    state.ResumptionSamples = 0;
                    state.LastResumptionUtc = default(DateTime);
                }
                state.LastUtc = s.TimeUtc;

                // Detect a meaningful pullback toward EMA9 while the H1 EMA regime
                // remains aligned; this is not an entry instruction.
                double distanceSigned = sign * (exitExecutable - s.Ema9) / s.Atr;
                bool pullbackNear9 = aligned && priceBeyond21 &&
                    distanceSigned >= -0.12 && distanceSigned <= 0.25;
                if (pullbackNear9)
                {
                    if (state.PullbackFirstUtc == default(DateTime))
                    {
                        state.PullbackFirstUtc = s.TimeUtc;
                        state.PullbackExtreme = mid;
                        state.ResumptionSamples = 0;
                    }
                    state.PullbackExtreme = buy
                        ? Math.Min(state.PullbackExtreme, mid)
                        : Math.Max(state.PullbackExtreme, mid);
                }

                if (state.PullbackFirstUtc != default(DateTime))
                {
                    if (s.TimeUtc - state.PullbackFirstUtc > TimeSpan.FromMinutes(30) || !aligned || deep21Breach)
                    {
                        state.PullbackFirstUtc = default(DateTime);
                        state.ResumptionSamples = 0;
                    }
                    else
                    {
                        double resumedAtr = sign * (mid - state.PullbackExtreme) / s.Atr;
                        bool meaningfulResumption = resumedAtr >= 0.18 &&
                            sign * (mid - s.Ema9) >= 0.05 * s.Atr &&
                            (!s.HasM15Data || s.M15Aligned);
                        if (meaningfulResumption &&
                            (state.LastResumptionUtc == default(DateTime) ||
                             s.TimeUtc - state.LastResumptionUtc >= TimeSpan.FromSeconds(10)))
                        {
                            state.LastResumptionUtc = s.TimeUtc;
                            state.ResumptionSamples++;
                        }
                        else if (!meaningfulResumption)
                        {
                            state.ResumptionSamples = 0;
                        }
                        a.Continuation = state.ResumptionSamples >= 2
                            ? ContinuationState.ConfirmedResumption
                            : meaningfulResumption ? ContinuationState.PossibleResumption
                            : ContinuationState.PullbackObserved;
                    }
                }
            }

            bool wallSafe = !s.HasOpposingSrData || !s.OpposingSrWallAhead ||
                            (Finite(s.OpposingSrClearanceAtr) && s.OpposingSrClearanceAtr >= 0.30);
            a.WouldConsiderFreshContinuation =
                a.Continuation == ContinuationState.ConfirmedResumption &&
                a.Location != EntryLocation.ExhaustedShallowBreak &&
                a.Health != TrendHealth.StructuralWarning &&
                s.HasM15Data && s.M15Aligned && wallSafe;
            a.Reason = "Candidate assessment; final execution requires existing FastEntry and TradeService gates.";
            return a;
        }

        public void Reset(string symbol, Direction side)
        {
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                Observation discarded;
                _observations.TryRemove(symbol + "|" + side, out discarded);
            }
        }

        private static Assessment Invalid(Assessment a, string reason)
        {
            a.Valid = false;
            a.Reason = reason;
            return a;
        }
        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        private static bool Positive(double x) => Finite(x) && x > 0;
    }
}
