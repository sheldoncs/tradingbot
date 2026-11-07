using System;
using System.Collections.Generic;
using System.Globalization;

namespace MetaTrader.Services.Helper
{
    /// <summary>
    /// Deterministic pre-entry failure screening for impulse candidates.
    /// This class never sends orders or estimates an empirical failure probability;
    /// its ShouldRejectLiveEntry result is consumed by FastEntryEngine.
    /// Feed it only data known at the candidate's decision time (avoid look-ahead).
    /// </summary>
    public static class LiveImpulseFailurePredictor
    {
        public enum Direction { Buy, Sell }
        public enum WarningLevel { Incomplete, Low, Elevated, High }

        public sealed class Settings
        {
            // Starting hypotheses, NOT parameters established by historical validation.
            public double ExtendedMoveAtr { get; set; } = 0.65;
            public double MomentumDropFraction { get; set; } = 0.20;
            public double OpposingWickRatio { get; set; } = 0.35;
            public double RejectionPullbackAtr { get; set; } = 0.12;
            public double NearWallAtr { get; set; } = 0.35;
            public double MinimumMomentumMagnitude { get; set; } = 0.05;
            public int ElevatedScore { get; set; } = 30;
            public int HighScore { get; set; } = 60;
            public bool RequireAllFactorsForGrade { get; set; } = true;
        }

        /// <summary>
        /// Bid-side OHLC prices must use the same quote convention. For execution use Ask for
        /// BUY and Bid for SELL in the caller; this class uses Bid for candle geometry.
        /// Distances (not profits) are in symbol price units unless named *Atr.
        /// </summary>
        public sealed class Snapshot
        {
            public string Symbol { get; set; } = string.Empty;
            public Direction Side { get; set; }
            public DateTime TimestampUtc { get; set; }
            public double Atr { get; set; }
            public double Bid { get; set; }
            public double Ask { get; set; }
            public double LiveOpen { get; set; }
            public double LiveHigh { get; set; }
            public double LiveLow { get; set; }

            // Non-null only when observed or calculated using data available at entry.
            // Both momenta are nonnegative magnitudes in the proposed trade direction.
            public double? CurrentMomentum { get; set; }
            public double? PreviousMomentum { get; set; }
            public double? CompletedH1Ema9 { get; set; }
            public double? CompletedH1Ema21 { get; set; }
            public double? PriceFiveMinutesAgo { get; set; }
            public double? CompressionBoundary { get; set; }
            // Nearest opposing SR wall clearance, already normalized by ATR.
            // Null means NOT KNOWN (not "no wall"). Positive means room ahead.
            public double? OpposingWallClearanceAtr { get; set; }
        }

        public sealed class Factor
        {
            public string Code { get; internal set; } = string.Empty;
            public string Explanation { get; internal set; } = string.Empty;
            public int Weight { get; internal set; }
            public bool? Triggered { get; internal set; }
        }

        public sealed class Assessment
        {
            public string Symbol { get; internal set; } = string.Empty;
            public Direction Side { get; internal set; }
            public DateTime TimestampUtc { get; internal set; }
            public int ObservedScore { get; internal set; }
            public int UnknownWeight { get; internal set; }
            public int MinimumScore { get { return ObservedScore; } }
            public int MaximumPossibleScore { get { return ObservedScore + UnknownWeight; } }
            public WarningLevel Level { get; internal set; }
            public bool IsComplete { get { return UnknownWeight == 0; } }
            public double? FiveMinuteDirectionalMoveAtr { get; internal set; }
            public double? OpposingWickRatio { get; internal set; }
            public double? RejectionPullbackAtr { get; internal set; }
            public IReadOnlyList<Factor> Factors { get; internal set; } = Array.Empty<Factor>();

            public string ToLogLine()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "[{0}] IMPULSE FAILURE STUDY | side={1} | level={2} | score={3}/100 | possibleMax={4}/100 | complete={5} | move5mATR={6} | wickRatio={7} | pullbackATR={8} | factors={9} | mode=LIVE_GUARD",
                    Symbol, Side, Level, ObservedScore, MaximumPossibleScore, IsComplete,
                    Format(FiveMinuteDirectionalMoveAtr), Format(OpposingWickRatio),
                    Format(RejectionPullbackAtr), string.Join(",", SummarizeFactors(Factors)));
            }

            private static string Format(double? value)
            {
                return value.HasValue ? value.Value.ToString("F3", CultureInfo.InvariantCulture) : "NA";
            }
            private static IEnumerable<string> SummarizeFactors(IEnumerable<Factor> factors)
            {
                foreach (var f in factors)
                    yield return f.Code + ":" + (f.Triggered.HasValue ? (f.Triggered.Value ? "WARN" : "OK") : "NA");
            }
        }

        /// <summary>
        /// Active, deliberately narrow pre-entry veto. Requires confirmed extension
        /// AND confirmed rejection AND either fading momentum or countertrend H1.
        /// Unknown evidence NEVER counts as triggered. A high numerical warning
        /// score alone is insufficient. These are heuristic conditions, NOT a
        /// validated failure probability; demo/forward testing remains essential.
        /// </summary>
        public static bool ShouldRejectLiveEntry(Assessment assessment)
        {
            if (assessment == null) return false;
            return IsTriggered(assessment, "EXTENDED") &&
                   IsTriggered(assessment, "REJECTION") &&
                   (IsTriggered(assessment, "FADING") ||
                    IsTriggered(assessment, "H1_CONFLICT"));
        }

        private static bool IsTriggered(Assessment assessment, string code)
        {
            if (assessment.Factors == null) return false;
            foreach (Factor factor in assessment.Factors)
            {
                if (factor.Code == code)
                    return factor.Triggered == true;
            }
            return false;
        }

        public static Assessment Evaluate(Snapshot snapshot, Settings settings = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            settings = settings ?? new Settings();
            Validate(settings);
            if (!Finite(snapshot.Atr) || snapshot.Atr <= 0)
                throw new ArgumentOutOfRangeException(nameof(snapshot.Atr), "ATR must be positive and finite.");
            if (!Finite(snapshot.Bid) || snapshot.Bid <= 0 ||
                !Finite(snapshot.Ask) || snapshot.Ask < snapshot.Bid)
                throw new ArgumentException("Bid/Ask must be finite, positive and Ask >= Bid.");
            if (!Finite(snapshot.LiveOpen) || !Finite(snapshot.LiveHigh) || !Finite(snapshot.LiveLow) ||
                snapshot.LiveLow > snapshot.LiveHigh)
                throw new ArgumentException("Live OHLC must be finite and High >= Low.");
            // Prices may differ by a tick due to a concurrent quote update; validate only
            // that the provided candle is internally consistent, not that bid == its close.
            if (snapshot.LiveOpen < snapshot.LiveLow || snapshot.LiveOpen > snapshot.LiveHigh)
                throw new ArgumentException("LiveOpen must be within the live high/low range.");
            if (!Enum.IsDefined(typeof(Direction), snapshot.Side))
                throw new ArgumentException("Unsupported direction.", nameof(snapshot));

            double dir = snapshot.Side == Direction.Buy ? 1.0 : -1.0;
            var factors = new List<Factor>(5);
            double? moveAtr = null;
            if (ValidPositive(snapshot.PriceFiveMinutesAgo))
                moveAtr = dir * (snapshot.Bid - snapshot.PriceFiveMinutesAgo.Value) / snapshot.Atr;

            // The directional move from 5 minutes ago is always the baseline. When a
            // compression boundary is supplied, extension beyond it can ALSO flag a late entry.
            double? breakExtensionAtr = null;
            if (ValidPositive(snapshot.CompressionBoundary))
                breakExtensionAtr = dir * (snapshot.Bid - snapshot.CompressionBoundary.Value) / snapshot.Atr;
            bool? extended = null;
            if (moveAtr.HasValue || breakExtensionAtr.HasValue)
                extended = (moveAtr.HasValue && moveAtr.Value >= settings.ExtendedMoveAtr) ||
                           (breakExtensionAtr.HasValue && breakExtensionAtr.Value >= settings.ExtendedMoveAtr);
            factors.Add(Make("EXTENDED", 25, extended, "Directional run-up / breakout extension already large"));

            bool? momentumFading = null;
            if (ValidNonnegative(snapshot.CurrentMomentum) && ValidNonnegative(snapshot.PreviousMomentum))
            {
                double prev = snapshot.PreviousMomentum.Value;
                double now = snapshot.CurrentMomentum.Value;
                // Avoid classifying near-zero noise as meaningful deceleration.
                momentumFading = prev >= settings.MinimumMomentumMagnitude &&
                    now <= prev * (1.0 - settings.MomentumDropFraction);
            }
            factors.Add(Make("FADING", 25, momentumFading, "Directional momentum magnitude has decreased"));

            double range = snapshot.LiveHigh - snapshot.LiveLow;
            double bodyTop = Math.Max(snapshot.LiveOpen, snapshot.Bid);
            double bodyBottom = Math.Min(snapshot.LiveOpen, snapshot.Bid);
            // Tick updates may cause Bid to be just outside the supplied high/low.
            // When that happens, mark the wick factor unknown, never silently clear it.
            bool validLiveQuote = snapshot.Bid >= snapshot.LiveLow && snapshot.Bid <= snapshot.LiveHigh;
            double? wickRatio = null, retreatAtr = null;
            bool? rejecting = null;
            if (validLiveQuote && range > 0)
            {
                double opposingWick = snapshot.Side == Direction.Buy
                    ? Math.Max(0.0, snapshot.LiveHigh - bodyTop)
                    : Math.Max(0.0, bodyBottom - snapshot.LiveLow);
                double pullback = snapshot.Side == Direction.Buy
                    ? Math.Max(0.0, snapshot.LiveHigh - snapshot.Bid)
                    : Math.Max(0.0, snapshot.Bid - snapshot.LiveLow);
                wickRatio = opposingWick / range;
                retreatAtr = pullback / snapshot.Atr;
                rejecting = wickRatio >= settings.OpposingWickRatio ||
                            retreatAtr >= settings.RejectionPullbackAtr;
            }
            factors.Add(Make("REJECTION", 20, rejecting, "Live opposing wick or retreat from extreme"));

            bool? h1Conflict = null;
            if (ValidPositive(snapshot.CompletedH1Ema9) && ValidPositive(snapshot.CompletedH1Ema21))
            {
                double spread = snapshot.CompletedH1Ema9.Value - snapshot.CompletedH1Ema21.Value;
                // Exact equality is neutral, not opposition.
                h1Conflict = dir * spread < 0;
            }
            factors.Add(Make("H1_CONFLICT", 15, h1Conflict, "Completed H1 EMA9/EMA21 direction opposes impulse"));

            bool? closeWall = null;
            if (Valid(snapshot.OpposingWallClearanceAtr))
            {
                // Zero or negative clearance means touching/beyond an opposing wall.
                closeWall = snapshot.OpposingWallClearanceAtr.Value <= settings.NearWallAtr;
            }
            factors.Add(Make("NEAR_WALL", 15, closeWall, "Opposing support/resistance too close"));

            int score = 0, unknown = 0;
            foreach (Factor factor in factors)
            {
                if (!factor.Triggered.HasValue) unknown += factor.Weight;
                else if (factor.Triggered.Value) score += factor.Weight;
            }
            var level = settings.RequireAllFactorsForGrade && unknown > 0
                ? WarningLevel.Incomplete
                : score >= settings.HighScore ? WarningLevel.High
                : score >= settings.ElevatedScore ? WarningLevel.Elevated
                : WarningLevel.Low;

            return new Assessment
            {
                Symbol = snapshot.Symbol ?? string.Empty,
                Side = snapshot.Side,
                TimestampUtc = snapshot.TimestampUtc,
                ObservedScore = score,
                UnknownWeight = unknown,
                Level = level,
                Factors = factors.AsReadOnly(),
                FiveMinuteDirectionalMoveAtr = moveAtr,
                OpposingWickRatio = wickRatio,
                RejectionPullbackAtr = retreatAtr
            };
        }

        private static Factor Make(string code, int weight, bool? triggered, string explanation)
        {
            return new Factor { Code = code, Weight = weight, Triggered = triggered, Explanation = explanation };
        }
        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        private static bool Valid(double? v) { return v.HasValue && Finite(v.Value); }
        private static bool ValidPositive(double? v) { return Valid(v) && v.Value > 0; }
        private static bool ValidNonnegative(double? v) { return Valid(v) && v.Value >= 0; }
        private static void Validate(Settings s)
        {
            if (!Finite(s.ExtendedMoveAtr) || s.ExtendedMoveAtr <= 0 ||
                !Finite(s.MomentumDropFraction) || s.MomentumDropFraction <= 0 || s.MomentumDropFraction >= 1 ||
                !Finite(s.OpposingWickRatio) || s.OpposingWickRatio <= 0 || s.OpposingWickRatio > 1 ||
                !Finite(s.RejectionPullbackAtr) || s.RejectionPullbackAtr <= 0 ||
                !Finite(s.NearWallAtr) || s.NearWallAtr < 0 ||
                !Finite(s.MinimumMomentumMagnitude) || s.MinimumMomentumMagnitude < 0 ||
                s.ElevatedScore < 0 || s.HighScore > 100 || s.ElevatedScore >= s.HighScore)
                throw new ArgumentException("Invalid predictor settings.", nameof(s));
        }
    }
}
