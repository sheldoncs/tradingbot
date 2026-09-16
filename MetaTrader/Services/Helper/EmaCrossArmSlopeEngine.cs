namespace MetaTrader.Services.Helper
{
    using System;
    using System.Collections.Concurrent;

    public static class EmaCrossArmSlopeEngine
    {
        // ============================================================
        // PUBLIC RESULT
        // ============================================================

        public enum ArmedDirection
        {
            None,
            Buy,
            Sell
        }

        public class CrossArmResult
        {
            public ArmedDirection Direction { get; set; }

            public bool IsArmed { get; set; }

            public bool BuyAllowed { get; set; }

            public bool SellAllowed { get; set; }

            public bool BullishCrossDetected { get; set; }

            public bool BearishCrossDetected { get; set; }

            public bool Ema9Rising { get; set; }

            public bool Ema9Falling { get; set; }

            public bool CorrectAlignment { get; set; }

            public bool WindowExpired { get; set; }

            public DateTime ArmedAtUtc { get; set; }

            public double AgeSeconds { get; set; }

            public string Reason { get; set; }
        }


        // ============================================================
        // INTERNAL STATE
        // ============================================================

        private class SymbolArmState
        {
            public ArmedDirection Direction;

            public DateTime ArmedAtUtc;

            // -1 = EMA9 below EMA21
            //  0 = equal / unknown
            // +1 = EMA9 above EMA21
            public int LastRelation;

            public double LastLiveEma9;

            // True only after a genuine cross arm has expired.
            // This allows the SAME crossover regime to arm again
            // without requiring EMA9 to cross EMA21 a second time.
            public bool RearmEligible;

            // Relationship that was active when the arm expired.
            // +1 = bullish regime, -1 = bearish regime.
            public int RearmRelation;

            public bool Initialized;
        }


        private static readonly ConcurrentDictionary<string, SymbolArmState>
            _states = new ConcurrentDictionary<string, SymbolArmState>();


        // ============================================================
        // MAIN EVALUATION
        // ============================================================

        public static CrossArmResult Evaluate(
            string symbol,
            double liveEma9,
            double liveEma21,
            double closedEma9,
            double closedEma21,
            TimeSpan armWindow)
        {
            var result = new CrossArmResult
            {
                Direction = ArmedDirection.None,
                IsArmed = false,
                BuyAllowed = false,
                SellAllowed = false,
                Reason = "Not evaluated"
            };


            // ========================================================
            // BASIC VALIDATION
            // ========================================================

            if (string.IsNullOrWhiteSpace(symbol))
            {
                result.Reason = "Invalid symbol";
                return result;
            }

            if (liveEma9 <= 0 ||
                liveEma21 <= 0 ||
                closedEma9 <= 0 ||
                closedEma21 <= 0)
            {
                result.Reason = "Invalid EMA values";
                return result;
            }

            if (armWindow <= TimeSpan.Zero)
            {
                result.Reason = "Invalid arm window";
                return result;
            }


            string key =
                NormalizeSymbol(symbol);


            SymbolArmState state =
                _states.GetOrAdd(
                    key,
                    _ => new SymbolArmState()
                );


            // ========================================================
            // CURRENT LIVE EMA RELATIONSHIP
            // ========================================================

            int currentRelation =
                GetRelation(
                    liveEma9,
                    liveEma21
                );


            int closedRelation =
                GetRelation(
                    closedEma9,
                    closedEma21
                );


            // ========================================================
            // LIVE EMA9 SLOPE
            //
            // We compare LIVE EMA9 against the latest CLOSED EMA9.
            //
            // This avoids using raw price direction as EMA slope.
            // ========================================================

            bool ema9Rising =
                liveEma9 >
                closedEma9;


            bool ema9Falling =
                liveEma9 <
                closedEma9;


            result.Ema9Rising =
                ema9Rising;

            result.Ema9Falling =
                ema9Falling;


            // ========================================================
            // FIRST CALL INITIALIZATION
            //
            // Important:
            //
            // If the previous CLOSED EMA relationship was bearish
            // but the current LIVE relationship is bullish, then
            // a bullish cross has occurred inside the live candle.
            //
            // Same logic for bearish cross.
            // ========================================================

            if (!state.Initialized)
            {
                state.LastRelation =
                    closedRelation;

                state.LastLiveEma9 =
                    closedEma9;

                state.Initialized =
                    true;
            }


            // ========================================================
            // DETECT ACTUAL RELATIONSHIP TRANSITION
            //
            // Bullish:
            //
            // EMA9 was at/below EMA21
            // and is now above EMA21.
            //
            // Bearish:
            //
            // EMA9 was at/above EMA21
            // and is now below EMA21.
            // ========================================================

            bool bullishCross =
                currentRelation > 0 &&
                state.LastRelation <= 0;


            bool bearishCross =
                currentRelation < 0 &&
                state.LastRelation >= 0;


            result.BullishCrossDetected =
                bullishCross;

            result.BearishCrossDetected =
                bearishCross;


            // ========================================================
            // ARM ON CROSS
            //
            // Require slope confirmation AT THE CROSS.
            //
            // Bullish cross:
            //      EMA9 > EMA21
            //      EMA9 rising
            //
            // Bearish cross:
            //      EMA9 < EMA21
            //      EMA9 falling
            // ========================================================

            if (bullishCross &&
                ema9Rising)
            {
                state.Direction =
                    ArmedDirection.Buy;

                state.ArmedAtUtc =
                    DateTime.UtcNow;

                state.RearmEligible =
                    false;

                state.RearmRelation =
                    0;
            }
            else if (bearishCross &&
                     ema9Falling)
            {
                state.Direction =
                    ArmedDirection.Sell;

                state.ArmedAtUtc =
                    DateTime.UtcNow;

                state.RearmEligible =
                    false;

                state.RearmRelation =
                    0;
            }


            // ========================================================
            // OPPOSITE CROSS CANCELS EXISTING ARM
            //
            // Even if slope has not confirmed the new direction yet,
            // the old direction must no longer remain armed.
            // ========================================================

            if (bearishCross &&
                state.Direction == ArmedDirection.Buy)
            {
                state.Direction =
                    ArmedDirection.None;

                state.ArmedAtUtc =
                    DateTime.MinValue;

                state.RearmEligible =
                    false;

                state.RearmRelation =
                    0;
            }


            if (bullishCross &&
                state.Direction == ArmedDirection.Sell)
            {
                state.Direction =
                    ArmedDirection.None;

                state.ArmedAtUtc =
                    DateTime.MinValue;

                state.RearmEligible =
                    false;

                state.RearmRelation =
                    0;
            }


            // ========================================================
            // WINDOW EXPIRATION
            // ========================================================

            bool expired =
                false;


            double ageSeconds =
                0.0;


            if (state.Direction != ArmedDirection.None &&
                state.ArmedAtUtc != DateTime.MinValue)
            {
                TimeSpan age =
                    DateTime.UtcNow -
                    state.ArmedAtUtc;


                ageSeconds =
                    age.TotalSeconds;


                if (age > armWindow)
                {
                    expired =
                        true;

                    // Remember the EMA relationship that belonged to
                    // the genuine crossover that just expired.
                    //
                    // This is what lets the SAME crossover regime
                    // become eligible for another trading window.
                    state.RearmEligible =
                        currentRelation != 0;

                    state.RearmRelation =
                        currentRelation;

                    state.Direction =
                        ArmedDirection.None;

                    state.ArmedAtUtc =
                        DateTime.MinValue;
                }
            }


            // ========================================================
            // RE-ARM SAME CROSSOVER REGIME AFTER EXPIRY
            //
            // This does NOT create a fresh cross.
            //
            // A re-arm is allowed only when:
            //
            // 1. A genuine cross was armed previously.
            // 2. That arm window expired.
            // 3. EMA9 is still on the SAME side of EMA21.
            // 4. EMA9 still has the correct slope.
            // 5. EMA9 is actively continuing in that direction
            //    compared with the previous live evaluation.
            //
            // The downstream impulse / continuation / S/R filters
            // still decide whether an actual trade is entered.
            // ========================================================

            bool rearmedSameRegime =
                false;


            bool liveEma9ContinuingUp =
                state.LastLiveEma9 > 0 &&
                liveEma9 > state.LastLiveEma9;


            bool liveEma9ContinuingDown =
                state.LastLiveEma9 > 0 &&
                liveEma9 < state.LastLiveEma9;


            if (state.Direction == ArmedDirection.None &&
                state.RearmEligible &&
                state.RearmRelation == currentRelation)
            {
                if (currentRelation > 0 &&
                    ema9Rising &&
                    liveEma9ContinuingUp)
                {
                    state.Direction =
                        ArmedDirection.Buy;

                    state.ArmedAtUtc =
                        DateTime.UtcNow;

                    state.RearmEligible =
                        false;

                    state.RearmRelation =
                        0;

                    rearmedSameRegime =
                        true;

                    ageSeconds =
                        0.0;
                }
                else if (currentRelation < 0 &&
                         ema9Falling &&
                         liveEma9ContinuingDown)
                {
                    state.Direction =
                        ArmedDirection.Sell;

                    state.ArmedAtUtc =
                        DateTime.UtcNow;

                    state.RearmEligible =
                        false;

                    state.RearmRelation =
                        0;

                    rearmedSameRegime =
                        true;

                    ageSeconds =
                        0.0;
                }
            }


            // If the EMA relationship changes while we are waiting to
            // re-arm, the old crossover regime is no longer valid.
            if (state.RearmEligible &&
                state.RearmRelation != 0 &&
                currentRelation != state.RearmRelation)
            {
                state.RearmEligible =
                    false;

                state.RearmRelation =
                    0;
            }


            result.WindowExpired =
                expired;

            result.AgeSeconds =
                ageSeconds;


            // ========================================================
            // FINAL BUY / SELL GUARD
            //
            // The arm by itself is NOT enough.
            //
            // BUY requires:
            //      BUY arm
            //      EMA9 still above EMA21
            //      EMA9 still rising
            //
            // SELL requires:
            //      SELL arm
            //      EMA9 still below EMA21
            //      EMA9 still falling
            // ========================================================

            bool buyAllowed =
                state.Direction == ArmedDirection.Buy &&
                liveEma9 > liveEma21 &&
                ema9Rising;


            bool sellAllowed =
                state.Direction == ArmedDirection.Sell &&
                liveEma9 < liveEma21 &&
                ema9Falling;


            result.Direction =
                state.Direction;

            result.IsArmed =
                state.Direction != ArmedDirection.None;

            result.BuyAllowed =
                buyAllowed;

            result.SellAllowed =
                sellAllowed;


            result.CorrectAlignment =
                buyAllowed ||
                sellAllowed;


            result.ArmedAtUtc =
                state.ArmedAtUtc;


            // ========================================================
            // DIAGNOSTIC REASON
            // ========================================================

            if (bullishCross &&
                ema9Rising)
            {
                result.Reason =
                    "Bullish EMA9/EMA21 cross detected - BUY armed";
            }
            else if (bearishCross &&
                     ema9Falling)
            {
                result.Reason =
                    "Bearish EMA9/EMA21 cross detected - SELL armed";
            }
            else if (rearmedSameRegime &&
                     state.Direction == ArmedDirection.Buy)
            {
                result.Reason =
                    "BUY re-armed after expiry - same bullish EMA regime";
            }
            else if (rearmedSameRegime &&
                     state.Direction == ArmedDirection.Sell)
            {
                result.Reason =
                    "SELL re-armed after expiry - same bearish EMA regime";
            }
            else if (expired)
            {
                result.Reason =
                    "Cross arm window expired - same regime eligible for re-arm";
            }
            else if (state.Direction == ArmedDirection.Buy)
            {
                if (liveEma9 <= liveEma21)
                {
                    result.Reason =
                        "BUY armed but EMA9 no longer above EMA21";
                }
                else if (!ema9Rising)
                {
                    result.Reason =
                        "BUY armed but EMA9 is not rising";
                }
                else
                {
                    result.Reason =
                        "BUY cross arm active";
                }
            }
            else if (state.Direction == ArmedDirection.Sell)
            {
                if (liveEma9 >= liveEma21)
                {
                    result.Reason =
                        "SELL armed but EMA9 no longer below EMA21";
                }
                else if (!ema9Falling)
                {
                    result.Reason =
                        "SELL armed but EMA9 is not falling";
                }
                else
                {
                    result.Reason =
                        "SELL cross arm active";
                }
            }
            else if (state.RearmEligible)
            {
                result.Reason =
                    "Cross arm expired - waiting for same-regime EMA9 continuation to re-arm";
            }
            else
            {
                result.Reason =
                    "Waiting for EMA9/EMA21 crossover";
            }


            // ========================================================
            // SAVE LIVE RELATIONSHIP FOR NEXT EVALUATION
            //
            // THIS IS IMPORTANT.
            //
            // Without this, the same cross could repeatedly reset the
            // arm timer on every FastEntry evaluation.
            // ========================================================

            state.LastRelation =
                currentRelation;

            state.LastLiveEma9 =
                liveEma9;


            return result;
        }


        // ============================================================
        // MANUAL RESET
        // ============================================================

        public static void Reset(
            string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;


            string key =
                NormalizeSymbol(symbol);


            _states.TryRemove(
                key,
                out _
            );
        }


        // ============================================================
        // RESET ALL
        // ============================================================

        public static void ResetAll()
        {
            _states.Clear();
        }


        // ============================================================
        // CURRENT ARMED DIRECTION
        // ============================================================

        public static ArmedDirection GetArmedDirection(
            string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return ArmedDirection.None;


            string key =
                NormalizeSymbol(symbol);


            if (_states.TryGetValue(
                key,
                out SymbolArmState state))
            {
                return state.Direction;
            }


            return ArmedDirection.None;
        }


        // ============================================================
        // EMA RELATIONSHIP
        // ============================================================

        private static int GetRelation(
            double ema9,
            double ema21)
        {
            if (ema9 > ema21)
                return 1;

            if (ema9 < ema21)
                return -1;

            return 0;
        }


        // ============================================================
        // SYMBOL NORMALIZATION
        // ============================================================

        private static string NormalizeSymbol(
            string symbol)
        {
            return
                symbol
                .Trim()
                .ToUpperInvariant();
        }
    }
}
