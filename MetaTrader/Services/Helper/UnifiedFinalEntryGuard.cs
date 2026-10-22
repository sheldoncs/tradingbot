namespace MetaTrader.Services.Helper
{
    using System;

    /// <summary>
    /// Reusable, side-effect-free FINAL market-entry LOCATION gate for Gold and FX.
    /// It does not place orders, acquire locks, read MT5, or manage open positions.
    /// Feed it a FRESH broker quote and structural levels immediately before OrderSend.
    /// If required evidence is missing it rejects the entry (fail closed).
    /// </summary>
    
    public static class UnifiedFinalEntryGuard
    {
        public enum Direction { Buy, Sell }

        public sealed class Request
        {
            public string Symbol { get; set; }
            public Direction Side { get; set; }
            public double Bid { get; set; }
            public double Ask { get; set; }
            public double Atr { get; set; }
            public double Ema9 { get; set; }
            public double Momentum { get; set; }
            public double PreviousMomentum { get; set; }

            // Set these from your SupportResistanceEngine using CLOSED H1 candles.
            // For BUY: the nearest relevant overhead resistance, or most recently crossed wall.
            // For SELL: the nearest relevant underlying support, or most recently crossed wall.
            // Use 0 only if an actual structure scan confirmed that no level was found.
            public double NearestWall { get; set; }
            public double NextWall { get; set; }   // Next overhead resistance for BUY / lower support for SELL; 0 if none.
            public bool StructureScanValid { get; set; }

            // Only mark true after a CLOSED M15/H1 bar genuinely broke the wall.
            // A live tick poking through a level is NOT confirmation.
            public bool BreakoutConfirmedByClosedBar { get; set; }
            // A genuine retest of that same wall/zone, verified by closed candles.
            public bool RetestConfirmedByClosedBar { get; set; }

            // Explicit upstream impulse approval (after zone clearance).
            public bool ApprovedImpulseBreakout { get; set; }
            // Verified, read-only armed S/R watch handoff. Valid only for GOLD.
            public bool VerifiedArmedGoldBreakout { get; set; }

            // Optional execution checks. StopLoss / TakeProfit = 0 skips RR validation.
            public double StopLoss { get; set; }
            public double TakeProfit { get; set; }
            public DateTime QuoteUtc { get; set; }
            public DateTime EvaluationUtc { get; set; }
        }

        public sealed class Decision
        {
            public bool Allowed { get; internal set; }
            public string Reason { get; internal set; }
            public double WallDistanceAtr { get; internal set; }
            public double EmaDistanceAtr { get; internal set; }
            public string Profile { get; internal set; }
        }

        private sealed class Profile
        {
            public string Name;
            public double RequiredRunwayAtr;
            public double CriticalRunwayAtr;
            public double ZoneHalfWidthAtr;
            public double MaxEmaDistanceAtr;
            public bool RejectDecliningMomentum;
            public double MinMomentum;
        }

        private static Profile GetProfile(string symbol)
        {
            string s = (symbol ?? "").ToUpperInvariant();
            if (s.Contains("XAU") || s.Contains("XAG") || s.Contains("GOLD") || s.Contains("SILVER"))
                return new Profile
                {
                    Name = "GOLD",
                    RequiredRunwayAtr = 0.40,
                    CriticalRunwayAtr = 0.25,
                    ZoneHalfWidthAtr = 0.12,
                    MaxEmaDistanceAtr = 1.25,
                    RejectDecliningMomentum = false,
                    MinMomentum = 0.0
                };
            string[] pairs = { "GBPUSD", "EURUSD", "USDJPY", "EURJPY", "USDCAD", "AUDUSD", "NZDUSD", "USDCHF", "EURGBP", "GBPJPY", "AUDJPY", "CADJPY" };
            foreach (string pair in pairs)
                if (s.Contains(pair))
                    return new Profile
                    {
                        Name = "FX",
                        RequiredRunwayAtr = 0.60,
                        CriticalRunwayAtr = 0.20,
                        ZoneHalfWidthAtr = 0.08,
                        MaxEmaDistanceAtr = 1.10,
                        RejectDecliningMomentum = true,
                        MinMomentum = 0.0
                    };
            return null;
        }

        private static Decision Reject(Decision d, string reason)
        {
            d.Allowed = false;
            d.Reason = reason;
            return d;
        }

        public static Decision Evaluate(Request r)
        {
            var d = new Decision { Allowed = false, Reason = "NOT_EVALUATED", WallDistanceAtr = double.NaN, EmaDistanceAtr = double.NaN };
            if (r == null) return Reject(d, "NULL_REQUEST");
            Profile p = GetProfile(r.Symbol);
            if (p == null) return Reject(d, "UNSUPPORTED_SYMBOL");
            d.Profile = p.Name;
            if (!IsFinite(r.Bid) || !IsFinite(r.Ask) || r.Bid <= 0 || r.Ask < r.Bid ||
                !IsFinite(r.Atr) || r.Atr <= 0 || !IsFinite(r.Ema9) || r.Ema9 <= 0 ||
                !IsFinite(r.Momentum) || !IsFinite(r.PreviousMomentum) ||
                !IsFinite(r.NearestWall) || !IsFinite(r.NextWall))
                return Reject(d, "INVALID_MARKET_DATA");
            if (!r.StructureScanValid) return Reject(d, "STRUCTURE_NOT_VALIDATED");
            if (r.QuoteUtc == default(DateTime) || r.EvaluationUtc == default(DateTime) ||
                r.QuoteUtc.Kind != DateTimeKind.Utc || r.EvaluationUtc.Kind != DateTimeKind.Utc ||
                (r.EvaluationUtc - r.QuoteUtc).TotalSeconds > 3.0 ||
                (r.EvaluationUtc - r.QuoteUtc).TotalSeconds < -1.0)
                return Reject(d, "STALE_OR_UNTIMED_QUOTE");

            // A buy is submitted at ASK; a sell at BID. Never use the midpoint here.
            double price = r.Side == Direction.Buy ? r.Ask : r.Bid;
            d.EmaDistanceAtr = Math.Abs(price - r.Ema9) / r.Atr;
            if (d.EmaDistanceAtr > p.MaxEmaDistanceAtr)
                return Reject(d, "EMA9_CHASE_DISTANCE");
            if (p.RejectDecliningMomentum && r.Momentum < r.PreviousMomentum)
                return Reject(d, "MOMENTUM_DECLINING");
            if (r.Momentum < p.MinMomentum)
                return Reject(d, "INSUFFICIENT_MOMENTUM");

            bool buy = r.Side == Direction.Buy;
            double wall = r.NearestWall;
            if (wall > 0)
            {
                double nearEdge = buy ? wall - p.ZoneHalfWidthAtr * r.Atr : wall + p.ZoneHalfWidthAtr * r.Atr;
                double farEdge = buy ? wall + p.ZoneHalfWidthAtr * r.Atr : wall - p.ZoneHalfWidthAtr * r.Atr;
                double signedNear = buy ? (nearEdge - price) / r.Atr : (price - nearEdge) / r.Atr;
                double signedFar = buy ? (price - farEdge) / r.Atr : (farEdge - price) / r.Atr;
                d.WallDistanceAtr = signedNear;

                if (signedNear > 0) // Before the wall: need sufficient runway.
                {
                    if (signedNear < p.CriticalRunwayAtr) return Reject(d, "CRITICAL_NO_RUNWAY_TO_WALL");
                    if (signedNear < p.RequiredRunwayAtr) return Reject(d, "INSUFFICIENT_RUNWAY_TO_WALL");
                }
                else // At/through the wall: a tick crossing alone is never sufficient.
                {
                    if (signedFar < 0) return Reject(d, "INSIDE_STRUCTURE_ZONE");
                    // An armed GOLD breakout was independently checked against
                    // the original remembered S/R wall at the executable quote.
                    // It requires the watch's 0.05 ATR outer-edge clearance;
                    // a generic approved impulse retains its stricter 0.15 ATR.
                    bool armedGoldBreak = p.Name == "GOLD" &&
                        r.VerifiedArmedGoldBreakout && signedFar >= 0.05;
                    bool qualifyingLiveBreak = r.ApprovedImpulseBreakout && signedFar >= 0.15;
                    if (!r.BreakoutConfirmedByClosedBar &&
                        !r.RetestConfirmedByClosedBar &&
                        !armedGoldBreak && !qualifyingLiveBreak)
                        return Reject(d, "UNCONFIRMED_STRUCTURE_BREAK");
                }
            }

            // Second wall prevents clearing the first wall straight into the next one.
            if (r.NextWall > 0)
            {
                double secondNearEdge = buy
                    ? r.NextWall - p.ZoneHalfWidthAtr * r.Atr
                    : r.NextWall + p.ZoneHalfWidthAtr * r.Atr;
                double secondSpaceAtr = buy ? (secondNearEdge - price) / r.Atr : (price - secondNearEdge) / r.Atr;
                if (secondSpaceAtr < p.RequiredRunwayAtr)
                    return Reject(d, "NEXT_STRUCTURE_WALL_TOO_CLOSE");
            }

            // Optional price-side and minimum reward/risk validation.
            if (r.StopLoss != 0 || r.TakeProfit != 0)
            {
                if (!IsFinite(r.StopLoss) || !IsFinite(r.TakeProfit)) return Reject(d, "INVALID_STOPS");
                double risk = buy ? price - r.StopLoss : r.StopLoss - price;
                double reward = buy ? r.TakeProfit - price : price - r.TakeProfit;
                if (risk <= 0 || reward <= 0) return Reject(d, "INVALID_SL_TP_DIRECTION");
                if (reward / risk < 1.0) return Reject(d, "REWARD_RISK_UNDER_1_TO_1");
            }
            d.Allowed = true;
            d.Reason = "FINAL_GATE_PASSED";
            return d;
        }

        private static bool IsFinite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
    }

}
